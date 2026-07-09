using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Wavee.Core;

namespace Wavee.Backend;

// ── The durable playback session — successor of QueueCore (F1–F11) ───────────────────────────────────────────────────
// One pure, single-threaded object. The SESSION is durable; user actions are cursor moves and edits within it (never a
// teardown-and-rebuild from the visible window). Every state transition returns the single atomic QueueSnapshot that the
// bridge/projection publishes — one truth, one revision. Ids are minted once at insertion and survive
// reorder/remove/continuation-append; q-uids are minted at add (the active device mints — FIXTURE-C); the full resolved
// context is held (no display caps live here — windowing is a bridge concern). QueueCore still exists until Integration
// swaps the controller over.

/// <summary>One resolved item in the session: a stable id + the domain track + its context uid + provider + row kind +
/// the context it came from + wire metadata. Immutable; the id is identity (position is presentation).</summary>
sealed record SessionItem(
    QueueItemId Id, Track Track, string Uid, QueueProvider Provider,
    QueueRowKind Kind, string? SourceContextUri,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>The one atomic read shape — serves the local AND viewer paths. Revision is a local monotonic bumped on every
/// mutation; <see cref="Current"/> is THE single source of "current"; the buckets are the full resolved state (windowing
/// is applied downstream, never here). <see cref="ClusterQueueRevision"/> is the cluster's string revision, echoed on an
/// outbound set_queue.</summary>
public sealed record QueueSnapshot(
    long Revision,
    string? ContextUri,
    string? AutoplayContextUri,
    QueueEntry? Current,
    ImmutableArray<QueueEntry> History,   // actually-played stack, newest last
    ImmutableArray<QueueEntry> UserQueue,
    ImmutableArray<QueueEntry> Upcoming,  // context + autoplay rows after the cursor (providers mark them)
    bool Shuffle, RepeatMode Repeat,
    string ClusterQueueRevision);

public sealed class PlaybackSession
{
    /// <summary>Actually-played rows retained (display windowing is a bridge concern — see §4.9).</summary>
    public const int HistoryCap = 32;

    readonly List<SessionItem> _context = new();       // the PLAY order (context rows + autoplay tail + markers)
    readonly List<SessionItem> _naturalOrder = new();  // the natural order — kept so shuffle can be turned back OFF
    readonly List<SessionItem> _userQueue = new();     // q# — drains before the context, cursor unmoved
    readonly List<SessionItem> _history = new();       // actually-played, newest last (cap HistoryCap)
    int _cursor = -1;                                  // index into _context of the resident context position (-1 = none)
    SessionItem? _current;                             // THE single source of "current" (may be a queue/history replay)
    ulong _nextItemId = 1;                             // 0 is QueueItemId.None — never mint it
    int _nextQueueUid;                                 // q{n} minting cursor (active device mints — FIXTURE-C)
    int _seedState = 0x5DEECE66 & 0x7fffffff;          // shuffle LCG (deterministic, replayable — no Random)
    long _revision;
    string? _contextUri;
    string? _autoplayContextUri;
    bool _shuffle;
    RepeatMode _repeat;
    string _clusterRevision = "";

    public string? ContextUri => _contextUri;
    public bool Shuffle => _shuffle;
    public RepeatMode Repeat => _repeat;
    public Track? Current => _current?.Track;

    /// <summary>Playable context rows remaining after the cursor (drives the continuation-prefetch trigger). Excludes the
    /// user queue and non-surfaced markers.</summary>
    public int RemainingInContext
    {
        get
        {
            int n = 0;
            for (int i = _cursor + 1; i < _context.Count; i++) if (_context[i].Kind == QueueRowKind.Playable) n++;
            return n;
        }
    }

    /// <summary>The next playable track that would surface on advance (user queue head → context row after cursor) — for
    /// fast-track warm-up. Null at the end of the resolved session.</summary>
    public Track? PeekNext()
    {
        for (int i = 0; i < _userQueue.Count; i++) if (_userQueue[i].Kind == QueueRowKind.Playable) return _userQueue[i].Track;
        for (int i = _cursor + 1; i < _context.Count; i++) if (_context[i].Kind == QueueRowKind.Playable) return _context[i].Track;
        return null;
    }

    /// <summary>The current track's uri + the most-recently-played uris (newest first), for the autoplay seed request.</summary>
    public IReadOnlyList<string> RecentUris(int max)
    {
        if (max <= 0) return Array.Empty<string>();
        var list = new List<string>(max);
        if (_current is { } cur && !string.IsNullOrEmpty(cur.Track.Uri)) list.Add(cur.Track.Uri);
        for (int i = _history.Count - 1; i >= 0 && list.Count < max; i--)
            if (!string.IsNullOrEmpty(_history[i].Track.Uri)) list.Add(_history[i].Track.Uri);
        return list;
    }

    // ── session lifecycle ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Seed the context (context-resolve / inbound play). keepUserQueue defaults true everywhere — starting a new
    /// context keeps the user queue (Spotify parity, FIXTURE-A); only an explicit transfer-in passes false. History is
    /// reset (a fresh context has no play history).</summary>
    public QueueSnapshot SetContext(string uri, IReadOnlyList<QueuedTrack> tracks, int startIndex, bool keepUserQueue = true)
    {
        _naturalOrder.Clear();
        _context.Clear();
        _history.Clear();
        _autoplayContextUri = null;
        _contextUri = uri;
        foreach (var q in tracks)
        {
            var it = new SessionItem(MintId(), q.Track, q.Uid, QueueProviderExtensions.FromWire(q.Provider),
                q.RowKind, uri, q.Metadata);
            _naturalOrder.Add(it);
            _context.Add(it);
        }
        if (!keepUserQueue) _userQueue.Clear();
        _cursor = _context.Count == 0 ? -1 : FirstPlayableFrom(Math.Clamp(startIndex, 0, _context.Count - 1));
        _current = _cursor >= 0 && _cursor < _context.Count ? _context[_cursor] : null;
        if (_shuffle) ReshuffleAnchoringCurrent();
        return Bump();
    }

    /// <summary>Relabel the context uri (server-side context patch / rename) without touching the resolved rows.</summary>
    public QueueSnapshot RelabelContext(string uri) { _contextUri = uri; return Bump(); }

    /// <summary>Append a resolved continuation page (lazy paging) or the autoplay-station tail. Autoplay pages set
    /// <see cref="QueueSnapshot.AutoplayContextUri"/> from the source uri.</summary>
    public QueueSnapshot AppendContextPage(IReadOnlyList<QueuedTrack> tracks, QueueProvider provider, string? sourceContextUri)
    {
        foreach (var q in tracks)
        {
            var it = new SessionItem(MintId(), q.Track, q.Uid, provider, q.RowKind, sourceContextUri, q.Metadata);
            _naturalOrder.Add(it);
            _context.Add(it);
        }
        if (provider == QueueProvider.Autoplay && !string.IsNullOrEmpty(sourceContextUri)) _autoplayContextUri = sourceContextUri;
        return Bump();
    }

    // ── cursor moves (skip-in-place; the session survives) ───────────────────────────────────────────────────────────

    /// <summary>Skip to an item by its stable id — routes to the Upcoming / UserQueue / History semantics (§4.3-4.5).
    /// Returns null on an identity miss (caller patches per §7.3.2) or a non-playable target.</summary>
    public QueueSnapshot? SkipToItem(QueueItemId id)
    {
        if (id.IsNone) return null;
        int k = _userQueue.FindIndex(x => x.Id == id);
        if (k >= 0) return SkipToUserQueueIndex(k);
        int j = _context.FindIndex(x => x.Id == id);
        if (j >= 0)
        {
            if (_context[j].Kind != QueueRowKind.Playable) return null;
            return j > _cursor ? SkipToUpcomingIndex(j) : SkipToContextBackIndex(j);
        }
        int h = _history.FindIndex(x => x.Id == id);
        if (h >= 0) return SkipToHistoryIndex(h);
        return null;
    }

    /// <summary>Skip by context uid (inbound next_track / remote row click) — uid-first identity, uri fallback. Returns
    /// null on a miss (caller may patch the clicked track in as current per §7.3.2).</summary>
    public QueueSnapshot? SkipToUid(string uid, string? uriFallback)
    {
        var byUid = LocateByUid(uid);
        if (byUid is { } r1) return SkipToLocated(r1);
        var byUri = LocateByUri(uriFallback);
        if (byUri is { } r2) return SkipToLocated(r2);
        return null;
    }

    /// <summary>Advance: user queue head → context row after cursor → autoplay tail → (repeat modes) → session end. A
    /// Delimiter row stops advance (advancing_past_track:"pause"); PageMarker/Delimiter rows are never surfaced (§4.6).</summary>
    public QueueSnapshot? Next()
    {
        if (_repeat == RepeatMode.Track && _current is { Kind: QueueRowKind.Playable }) return Bump();   // repeat-one holds
        PushHistory(_current);
        while (_userQueue.Count > 0)                                                                     // user queue drains first
        {
            var it = _userQueue[0];
            _userQueue.RemoveAt(0);
            if (it.Kind == QueueRowKind.Playable) { _current = it; return Bump(); }
        }
        while (_cursor + 1 < _context.Count)                                                             // context + autoplay spine
        {
            _cursor++;
            var it = _context[_cursor];
            if (it.Kind == QueueRowKind.Delimiter) { if (IsPauseDelimiter(it.Metadata)) { _current = null; return Bump(); } continue; }   // pause-delimiter stops; unmarked is transparent (§4.6)
            if (it.Kind == QueueRowKind.PageMarker) continue;                                            // transparent marker
            _current = it;
            return Bump();
        }
        if (_repeat == RepeatMode.Context)
        {
            for (_cursor = 0; _cursor < _context.Count; _cursor++)
            {
                var it = _context[_cursor];
                if (it.Kind == QueueRowKind.Delimiter) break;
                if (it.Kind == QueueRowKind.PageMarker) continue;
                _current = it;
                return Bump();
            }
        }
        _current = null;
        return Bump();   // end of session — the reducer decides whether to request autoplay
    }

    /// <summary>Step back to the most-recently-played row (cursor-back through history; else the prior context row).</summary>
    public QueueSnapshot? Prev()
    {
        if (_history.Count > 0) return SkipToHistoryIndex(_history.Count - 1);
        if (_cursor - 1 >= 0) { _cursor = PrevPlayableFrom(_cursor - 1); _current = _cursor >= 0 ? _context[_cursor] : _current; return Bump(); }
        return null;
    }

    // ── user-queue edits ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Append tracks to the user queue; each row's uid is minted "q{n}" at add (unless it already carries one —
    /// existing rows keep their uid; the active device mints only for uid:"" rows, §7.4).</summary>
    public QueueSnapshot EnqueueUser(IReadOnlyList<QueuedTrack> tracks)
    {
        foreach (var q in tracks) _userQueue.Add(NewQueueItem(q));
        return Bump();
    }

    /// <summary>Front-insert tracks ahead of the user queue ("play next"), preserving their order; same q-uid minting rule.</summary>
    public QueueSnapshot EnqueueNextUser(IReadOnlyList<QueuedTrack> tracks)
    {
        for (int i = 0; i < tracks.Count; i++) _userQueue.Insert(i, NewQueueItem(tracks[i]));
        return Bump();
    }

    /// <summary>Remove a user-queue row or an UPCOMING context row (index &gt; cursor) by id. Null if the id doesn't
    /// resolve to a removable row.</summary>
    public QueueSnapshot? RemoveItem(QueueItemId id)
    {
        if (id.IsNone) return null;
        int k = _userQueue.FindIndex(x => x.Id == id);
        if (k >= 0) { _userQueue.RemoveAt(k); return Bump(); }
        int j = _context.FindIndex(x => x.Id == id);
        if (j > _cursor && j < _context.Count)
        {
            _context.RemoveAt(j);
            _naturalOrder.RemoveAll(x => x.Id == id);
            return Bump();
        }
        return null;
    }

    /// <summary>Reorder a user-queue row (drag-to-reorder; the UI ships later). Null if the id isn't a user-queue row.</summary>
    public QueueSnapshot? MoveUserItem(QueueItemId id, int newPos)
    {
        int from = _userQueue.FindIndex(x => x.Id == id);
        if (from < 0) return null;
        var it = _userQueue[from];
        _userQueue.RemoveAt(from);
        _userQueue.Insert(Math.Clamp(newPos, 0, _userQueue.Count), it);
        return Bump();
    }

    /// <summary>Drop the whole user queue (the panel's "Clear" affordance, §10.1) — one revision bump; the context cursor,
    /// current and history are untouched. Local-session only (no wire verb exists).</summary>
    public QueueSnapshot ClearUserQueue() { _userQueue.Clear(); return Bump(); }

    /// <summary>Drop the play history (the History section's "Clear", §10.1) — one revision bump; current + context + user
    /// queue untouched. Local-session only.</summary>
    public QueueSnapshot ClearHistory() { _history.Clear(); return Bump(); }

    // ── options ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Toggle shuffle — anchored Fisher-Yates over the remaining context (current stays put); OFF restores the
    /// natural order and resumes from current's natural position.</summary>
    public QueueSnapshot SetShuffle(bool on)
    {
        if (on == _shuffle) return Bump();
        _shuffle = on;
        if (on) ReshuffleAnchoringCurrent();
        else RestoreOriginalOrder();
        return Bump();
    }

    public QueueSnapshot SetRepeat(RepeatMode mode) { _repeat = mode; return Bump(); }

    // ── inbound / recovery ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rebuild the whole session from a cluster (startup recovery / becoming active) — Current from player_state
    /// (provider-aware), UserQueue from provider:"queue" rows IN WIRE ORDER, Upcoming from the context+autoplay rows
    /// (markers kept as non-surfaced Kind rows). History is local-only (<see cref="PushHistory"/>); cluster prev_tracks
    /// are ignored until server-driven history lands. Cursor sits before Upcoming (=-1) so the current stands alone.</summary>
    public QueueSnapshot ReplaceFromCluster(ClusterDelta c, Track? hydratedCurrent)
    {
        _naturalOrder.Clear();
        _context.Clear();
        _userQueue.Clear();
        _history.Clear();
        _autoplayContextUri = null;
        _contextUri = c.ContextUri;
        _nextQueueUid = 0;

        foreach (var n in c.NextTracks)
        {
            var kind = RowKindOfUri(n.Uri);
            var provider = QueueProviderExtensions.FromWire(n.Provider);
            if (kind == QueueRowKind.Playable && provider == QueueProvider.Queue)
            {
                BumpQueueCursor(n.Uid);
                _userQueue.Add(new SessionItem(MintId(), TrackFromRemote(n), n.Uid, QueueProvider.Queue, kind, null, n.Metadata));
            }
            else
            {
                if (provider == QueueProvider.Autoplay && _autoplayContextUri is null) _autoplayContextUri = AutoplayCtxOf(n);
                var it = new SessionItem(MintId(), TrackFromRemote(n), n.Uid, provider, kind,
                    provider == QueueProvider.Autoplay ? _autoplayContextUri : c.ContextUri, n.Metadata);
                _naturalOrder.Add(it);
                _context.Add(it);
            }
        }
        _cursor = -1;
        if (hydratedCurrent is { } hc)
            _current = new SessionItem(MintId(), hc, c.Track.Uid, QueueProviderExtensions.FromWire(c.Track.Provider),
                QueueRowKind.Playable, c.ContextUri, c.Track.Metadata);
        else if (c.HasTrack)
            _current = new SessionItem(MintId(), TrackFromRemote(c.Track), c.Track.Uid,
                QueueProviderExtensions.FromWire(c.Track.Provider), QueueRowKind.Playable, c.ContextUri, c.Track.Metadata);
        else
            _current = null;

        _shuffle = c.Shuffle;
        _repeat = c.Repeat;
        _clusterRevision = c.QueueRevision ?? "";
        return Bump();
    }

    /// <summary>Apply an inbound set_queue: replace the user queue (provider:"queue" rows, uid-preserving; uid:"" mints)
    /// and the upcoming context/autoplay/marker rows. Local history is untouched (not driven by wire prev_tracks).
    /// Current is untouched (set_queue never changes the playing track).</summary>
    public QueueSnapshot ApplySetQueue(IReadOnlyList<QueueWireEntry> prev, IReadOnlyList<QueueWireEntry> next, string revision)
    {
        _clusterRevision = revision ?? "";
        _userQueue.Clear();
        _context.Clear();
        _naturalOrder.Clear();
        _autoplayContextUri = null;

        foreach (var e in next)
        {
            var kind = RowKindOfUri(e.Uri);
            if (kind == QueueRowKind.Playable && e.IsQueued)
            {
                string uid = e.Uid;
                if (string.IsNullOrEmpty(uid)) uid = "q" + (_nextQueueUid++);
                else BumpQueueCursor(uid);
                _userQueue.Add(new SessionItem(MintId(), ContextResolve.Synthetic(e.Uri), uid, QueueProvider.Queue, kind, null, e.Metadata));
            }
            else
            {
                var provider = kind != QueueRowKind.Playable ? QueueProvider.Context
                    : IsAutoplayMeta(e.Metadata) ? QueueProvider.Autoplay : QueueProvider.Context;
                if (provider == QueueProvider.Autoplay && _autoplayContextUri is null && e.Metadata is { } m && m.TryGetValue("context_uri", out var cu))
                    _autoplayContextUri = cu;
                var it = new SessionItem(MintId(), ContextResolve.Synthetic(e.Uri), e.Uid, provider, kind, _contextUri, e.Metadata);
                _naturalOrder.Add(it);
                _context.Add(it);
            }
        }
        _cursor = -1;
        return Bump();
    }

    // ── the ONLY read ────────────────────────────────────────────────────────────────────────────────────────────────

    public QueueSnapshot Snapshot()
    {
        QueueEntry? current = _current is { } c ? ToEntry(c, QueueBucket.NowPlaying) : null;

        var history = ImmutableArray.CreateBuilder<QueueEntry>(_history.Count);
        foreach (var it in _history) history.Add(ToEntry(it, QueueBucket.History));

        var userQueue = ImmutableArray.CreateBuilder<QueueEntry>(_userQueue.Count);
        foreach (var it in _userQueue) if (it.Kind == QueueRowKind.Playable) userQueue.Add(ToEntry(it, QueueBucket.UserQueue));

        var upcoming = ImmutableArray.CreateBuilder<QueueEntry>();
        for (int i = _cursor + 1; i < _context.Count; i++)
        {
            var it = _context[i];
            if (it.Kind != QueueRowKind.Playable) continue;   // delimiter/meta:page never surfaced
            upcoming.Add(ToEntry(it, QueueBucket.NextUp));
        }

        return new QueueSnapshot(
            _revision, _contextUri, _autoplayContextUri, current,
            history.ToImmutable(), userQueue.ToImmutable(), upcoming.ToImmutable(),
            _shuffle, _repeat, _clusterRevision);
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────────────────────────

    QueueSnapshot Bump() { _revision++; return Snapshot(); }

    QueueItemId MintId() => new(_nextItemId++);

    SessionItem NewQueueItem(QueuedTrack q)
    {
        string uid;
        if (!string.IsNullOrEmpty(q.Uid)) { uid = q.Uid; BumpQueueCursor(uid); }
        else uid = "q" + (_nextQueueUid++);
        return new SessionItem(MintId(), q.Track, uid, QueueProvider.Queue, q.RowKind, null, q.Metadata);
    }

    // Keep the q-uid mint cursor ahead of any preserved "q{n}" uid so a later local add never collides.
    void BumpQueueCursor(string uid)
    {
        if (uid.Length > 1 && uid[0] == 'q' && int.TryParse(uid.AsSpan(1), out int n) && n >= _nextQueueUid)
            _nextQueueUid = n + 1;
    }

    static QueueEntry ToEntry(SessionItem it, QueueBucket bucket) =>
        new(it.Id, "i" + it.Id.Value, it.Track, bucket, it.Provider,
            it.Provider == QueueProvider.Autoplay, it.Uid, it.Metadata);

    int FirstPlayableFrom(int start)
    {
        for (int i = start; i < _context.Count; i++) if (_context[i].Kind == QueueRowKind.Playable) return i;
        for (int i = start - 1; i >= 0; i--) if (_context[i].Kind == QueueRowKind.Playable) return i;
        return _context.Count > 0 ? Math.Clamp(start, 0, _context.Count - 1) : -1;
    }

    int PrevPlayableFrom(int start)
    {
        for (int i = start; i >= 0; i--) if (_context[i].Kind == QueueRowKind.Playable) return i;
        return -1;
    }

    // Skip to an UPCOMING context/autoplay row (§4.3): previous current → history; cursor jumps; skipped rows leave
    // Upcoming (they do NOT enter history — history is actually-played); user queue untouched.
    QueueSnapshot SkipToUpcomingIndex(int j)
    {
        PushHistory(_current);
        _cursor = j;
        _current = _context[j];
        return Bump();
    }

    // Skip BACK to an already-played context row still resident in _context (cursor-back within the context).
    QueueSnapshot SkipToContextBackIndex(int j)
    {
        int hi = _history.FindIndex(x => x.Id == _context[j].Id);
        if (hi >= 0) _history.RemoveRange(hi, _history.Count - hi);   // truncate history above it
        _cursor = j;
        _current = _context[j];
        return Bump();
    }

    // Skip to a USER-QUEUE row (§4.4): previous current → history; predecessors drain/drop (§13 decision 2); the target
    // becomes current (provider Queue); rows after it remain queued; the context cursor does not move.
    QueueSnapshot SkipToUserQueueIndex(int k)
    {
        PushHistory(_current);
        var it = _userQueue[k];
        _userQueue.RemoveRange(0, k + 1);
        _current = it;
        return Bump();
    }

    // Skip to a HISTORY row (§4.5, cursor-back): the entry becomes current; History truncates at/above it; a context row
    // re-derives Upcoming from its slot; a played queue/autoplay row replays one-shot with the context cursor unchanged;
    // user queue still drains first (untouched here).
    QueueSnapshot SkipToHistoryIndex(int h)
    {
        var entry = _history[h];
        _history.RemoveRange(h, _history.Count - h);
        int slot = _context.FindIndex(x => x.Id == entry.Id);
        if (entry.Provider == QueueProvider.Context && slot >= 0) { _cursor = slot; _current = _context[slot]; }
        else _current = entry;
        return Bump();
    }

    (char Bucket, int Index)? LocateByUid(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return null;
        int k = _userQueue.FindIndex(x => x.Uid == uid);
        if (k >= 0) return ('q', k);
        int j = _context.FindIndex(x => x.Uid == uid && x.Kind == QueueRowKind.Playable);
        if (j >= 0) return ('c', j);
        int h = _history.FindIndex(x => x.Uid == uid);
        if (h >= 0) return ('h', h);
        return null;
    }

    (char Bucket, int Index)? LocateByUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        int k = _userQueue.FindIndex(x => x.Track.Uri == uri);
        if (k >= 0) return ('q', k);
        int j = _context.FindIndex(x => x.Track.Uri == uri && x.Kind == QueueRowKind.Playable);
        if (j >= 0) return ('c', j);
        int h = _history.FindIndex(x => x.Track.Uri == uri);
        if (h >= 0) return ('h', h);
        return null;
    }

    QueueSnapshot SkipToLocated((char Bucket, int Index) r) => r.Bucket switch
    {
        'q' => SkipToUserQueueIndex(r.Index),
        'h' => SkipToHistoryIndex(r.Index),
        _ => r.Index > _cursor ? SkipToUpcomingIndex(r.Index) : SkipToContextBackIndex(r.Index),
    };

    void PushHistory(SessionItem? it)
    {
        if (it is null || it.Kind != QueueRowKind.Playable) return;
        if (_history.Count == 0 || _history[^1].Id != it.Id) _history.Add(it);
        if (_history.Count > HistoryCap) _history.RemoveRange(0, _history.Count - HistoryCap);
    }

    // Anchored Fisher-Yates over the natural order: the current context row stays at logical 0; only Context playable rows
    // are shuffled; the autoplay tail + markers keep their natural relative order at the end.
    void ReshuffleAnchoringCurrent()
    {
        if (_naturalOrder.Count <= 1) return;
        SessionItem? anchor = _current is { } c && _context.FindIndex(x => x.Id == c.Id) >= 0
            ? _current
            : (_cursor >= 0 && _cursor < _context.Count ? _context[_cursor] : null);
        if (anchor is null) return;

        var ctx = new List<SessionItem>();
        var tail = new List<SessionItem>();
        foreach (var it in _naturalOrder)
        {
            if (it.Id == anchor.Id) continue;
            if (it.Provider == QueueProvider.Context && it.Kind == QueueRowKind.Playable) ctx.Add(it);
            else tail.Add(it);
        }
        for (int i = ctx.Count - 1; i > 0; i--)
        {
            int j = NextSeed(i);
            (ctx[i], ctx[j]) = (ctx[j], ctx[i]);
        }
        _context.Clear();
        _context.Add(anchor);
        _context.AddRange(ctx);
        _context.AddRange(tail);
        _cursor = 0;
        if (_current is { } cur && cur.Id == anchor.Id) _current = anchor;
    }

    void RestoreOriginalOrder()
    {
        _context.Clear();
        _context.AddRange(_naturalOrder);
        int idx = _current is { } c ? _context.FindIndex(x => x.Id == c.Id) : -1;
        _cursor = idx >= 0 ? idx : (_context.Count > 0 ? 0 : -1);
    }

    int NextSeed(int maxInclusive)
    {
        _seedState = unchecked(_seedState * 1103515245 + 12345) & 0x7fffffff;
        return _seedState % (maxInclusive + 1);
    }

    static QueueRowKind RowKindOfUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return QueueRowKind.Playable;
        if (uri == "spotify:delimiter" || uri.Contains(":delimiter", StringComparison.Ordinal)) return QueueRowKind.Delimiter;
        if (uri.Contains(":meta:page", StringComparison.Ordinal)) return QueueRowKind.PageMarker;
        return QueueRowKind.Playable;
    }

    // A delimiter stops advance only when it carries advancing_past_track:"pause" (the wire signal Spotify sets when
    // autoplay is off, FIXTURE-A); an unmarked delimiter is a transparent boundary and is skipped (§4.6).
    static bool IsPauseDelimiter(IReadOnlyDictionary<string, string>? m) =>
        m is not null && ((m.TryGetValue("actions.advancing_past_track", out var a) && a == "pause")
                          || (m.TryGetValue("advancing_past_track", out var b) && b == "pause"));

    static bool IsAutoplayMeta(IReadOnlyDictionary<string, string>? m) =>
        m is not null && m.TryGetValue("autoplay.is_autoplay", out var v) && v == "true";

    static string? AutoplayCtxOf(in RemoteTrack n) =>
        n.Metadata is { } m && m.TryGetValue("context_uri", out var u) ? u : null;

    static Track TrackFromRemote(in RemoteTrack r)
    {
        var artists = string.IsNullOrEmpty(r.ArtistName) && string.IsNullOrEmpty(r.ArtistUri)
            ? Array.Empty<ArtistRef>()
            : new[] { new ArtistRef(IdOfUri(r.ArtistUri), r.ArtistUri, r.ArtistName) };
        var album = new AlbumRef(IdOfUri(r.AlbumUri), r.AlbumUri, r.AlbumName);
        var img = string.IsNullOrEmpty(r.ImageUrl) ? null : new Image(r.ImageUrl!);
        return new Track(IdOfUri(r.Uri), r.Uri, string.IsNullOrEmpty(r.Title) ? r.Uri : r.Title,
            artists, album, r.DurationMs, false, img);
    }

    static string IdOfUri(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "";
        int i = uri.LastIndexOf(':');
        return i >= 0 && i + 1 < uri.Length ? uri[(i + 1)..] : uri;
    }
}
