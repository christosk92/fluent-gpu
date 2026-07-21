using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The Core→engine bridge for the Mutations facet (docs/architecture.md §6 "a LibraryBridge") — the saved / liked /
/// followed set as a reactive <see cref="Signal{T}"/>. Subscribes the source's change stream, marshalling each callback
/// onto the UI thread via the post delegate. Toggles write OPTIMISTICALLY (the heart flips this frame) and the source
/// reconciles + re-emits the confirmed set. The heart / follow affordances read <see cref="Saved"/> (so they re-skin on
/// any change). Provided once at the app root via <see cref="Slot"/>; the saved-state federation hook is the registry's
/// <c>OfCapability(Mutations)</c> (a future multi-source FederatedMutations attaches there — deferred like Federated*).
/// </summary>
public sealed class LibraryBridge : IUndoTarget
{
    public static readonly Context<LibraryBridge?> Slot = new(null);

    readonly IMutationSource _mut;
    readonly UserPlaylistSource _playlists;
    readonly IPlaylistMutationSource _playlistEdits;
    readonly ActivityLog _activity;
    readonly List<IDisposable> _subs = [];
    readonly Dictionary<string, Signal<bool>> _savedByUri = new(StringComparer.Ordinal);
    bool _active;

    /// <summary>The saved-set — read by the heart / follow affordances (which subscribe → re-skin on any change).</summary>
    public Signal<IReadOnlySet<string>> Saved { get; }
    /// <summary>Bumps whenever a user playlist is created / added-to — the sidebar keys its playlist read on it to refresh.</summary>
    public Signal<int> PlaylistsVersion { get; } = new(0);

    public LibraryBridge(IMutationSource mut, UserPlaylistSource playlists, IPlaylistMutationSource playlistEdits, ActivityLog activity)
    {
        _mut = mut;
        _playlists = playlists;
        _playlistEdits = playlistEdits;
        _activity = activity;
        Saved = new Signal<IReadOnlySet<string>>(mut.Saved);
    }

    /// <summary>Subscribe the source's change stream → the signal. Idempotent. Call once from a mount effect with <c>UsePost()</c>.</summary>
    public void Activate(Action<Action> post)
    {
        if (_active) return;
        _active = true;
        _subs.Add(_mut.SavedChanged.Subscribe(s => post(() => PublishSaved(s))));
        _subs.Add(_playlists.PlaylistsChanged.Subscribe(v => post(() => PlaylistsVersion.Value = v)));
    }

    // ── playlist edits (create + add) ──────────────────────────────────────────────────────────────────────
    public async Task<string> CreatePlaylistAsync(string name, CancellationToken ct = default)
    {
        var uri = await _playlistEdits.CreatePlaylistAsync(name, ct).ConfigureAwait(false);
        if (!_activity.IsSuppressed) _activity.Record(ActivityKind.PlaylistCreate, uri, name);   // log-only (no Undo)
        return uri;
    }

    public string CreatePlaylist(string name)
    {
        var uri = _playlists.CreatePlaylist(name);
        if (!_activity.IsSuppressed) _activity.Record(ActivityKind.PlaylistCreate, uri, name);   // log-only (no Undo)
        return uri;
    }

    public void AddToPlaylist(string playlistUri, Track track)
    {
        _playlists.AddTrack(playlistUri, track);
        if (!_activity.IsSuppressed)
            _activity.Record(ActivityKind.PlaylistAddTracks, playlistUri, null, new ActivityPayload(Tracks: new[] { TrackRef(track) }));
    }

    /// <summary>Add tracks to ANY editable playlist by uri — the "Copy to playlist" picker's target. The mutation seam
    /// routes <c>wavee:playlist:*</c> to the local source and <c>spotify:playlist:*</c> to the real Spotify path; it
    /// fails loud (never silently no-ops) if a real backend isn't wired, which is intended.</summary>
    public Task AddTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.PlaylistAddTracks, playlistUri, null, PayloadFor(tracks));
        return WithFailure(_playlistEdits.AddTracksAsync(playlistUri, tracks, ct), id);
    }

    /// <summary>Add tracks to the user's default playlist (creating one if none) — the no-picker "Add to playlist".
    /// Returns the target (uri, name) for a confirmation toast.</summary>
    public (string Uri, string Name) AddToDefaultPlaylist(IEnumerable<Track> tracks)
    {
        var target = _playlists.DefaultPlaylist();
        var added = new List<Track>();
        foreach (var t in tracks) { _playlists.AddTrack(target.Uri, t); added.Add(t); }
        if (!_activity.IsSuppressed && added.Count > 0)
            _activity.Record(ActivityKind.PlaylistAddTracks, target.Uri, target.Name, PayloadFor(added));
        return target;
    }

    /// <summary>Is this uri saved / liked / followed? Reads the signal → subscribes the caller (live heart state).</summary>
    public bool IsSaved(string uri)
    {
        // Saved-state affordances subscribe to this URI only. A mutation elsewhere in the library must not re-render
        // every visible heart/card and turn a paint-sized state change into app-wide layout invalidation.
        if (!_savedByUri.TryGetValue(uri, out var state))
        {
            state = new Signal<bool>(Saved.Peek().Contains(uri));
            _savedByUri.Add(uri, state);
        }
        return state.Value;
    }

    /// <summary>Toggle saved-state with an OPTIMISTIC local flip (the heart updates this frame), then reconcile through
    /// the source (which re-emits the confirmed set). Called from a click handler, so the reads here don't subscribe.
    /// <paramref name="name"/> is display-only: it names the item in the notification-center activity entry.</summary>
    public void ToggleSaved(string uri, string? name = null) => SetSaved(uri, !Saved.Peek().Contains(uri), name);

    public void SetSaved(string uri, bool saved, string? name = null)
    {
        var cur = Saved.Peek();
        if (cur.Contains(uri) == saved) return;
        var next = new HashSet<string>(cur);
        if (saved) next.Add(uri); else next.Remove(uri);
        PublishSaved(next);                      // optimistic, URI-selective subscribers update this frame
        // Record BEFORE the async reconcile so the entry exists to flip Failed if the write faults immediately.
        long id = _activity.IsSuppressed ? -1 : _activity.Record(saved ? ActivityKind.Save : ActivityKind.Unsave, uri, name);
        var task = _mut.SetSavedAsync(uri, saved);   // reconcile (re-emits the confirmed set via the bridge subscription)
        if (id >= 0) _ = task.ContinueWith(t => { if (t.IsFaulted) _activity.MarkFailed(id); }, TaskScheduler.Default);
    }

    void PublishSaved(IReadOnlySet<string> next)
    {
        // Keep the aggregate snapshot for imperative reads/backwards compatibility. Equality suppression on each bool
        // means a full-set confirmation only wakes the affordance whose URI genuinely changed.
        Saved.Value = next;
        foreach (var (uri, state) in _savedByUri)
        {
            bool saved = next.Contains(uri);
            state.Value = saved;
        }
    }

    void IUndoTarget.SetSaved(string uri, bool saved) => SetSaved(uri, saved);

    // ── Spotify playlist editing ─────────────────────────────────────────────────────────────────────────
    /// <summary><paramref name="previousName"/> lets the rename be recorded (and undone) when the name actually changes;
    /// callers editing description/collaborative pass name=null so nothing is logged.</summary>
    public Task UpdatePlaylistDetailsAsync(string playlistUri, string? name, string? description, bool? collaborative,
        string? previousName = null, CancellationToken ct = default)
    {
        long id = -1;
        if (!_activity.IsSuppressed && name is { } newName && previousName is { } prev && !string.Equals(prev, newName, StringComparison.Ordinal))
            id = _activity.Record(ActivityKind.PlaylistRename, playlistUri, newName, new ActivityPayload(OldName: prev, NewName: newName));
        return WithFailure(_playlistEdits.UpdateDetailsAsync(playlistUri, name, description, collaborative, ct), id);
    }

    public Task SetPlaylistCoverJpegAsync(string playlistUri, byte[] jpeg, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.PlaylistCoverSet, playlistUri);   // log-only
        return WithFailure(_playlistEdits.SetCoverJpegAsync(playlistUri, jpeg, ct), id);
    }

    public Task ClearPlaylistCoverAsync(string playlistUri, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.PlaylistCoverClear, playlistUri);   // log-only
        return WithFailure(_playlistEdits.ClearCoverAsync(playlistUri, ct), id);
    }

    /// <summary><paramref name="removedTracks"/> (optional) captures the removed rows' uri/name so the remove can be undone
    /// by re-adding them; a null list still records the remove (log-only, undo fails cleanly).</summary>
    public Task RemovePlaylistRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows,
        IReadOnlyList<ActivityTrackRef>? removedTracks = null, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.PlaylistRemoveTracks, playlistUri, null, new ActivityPayload(Tracks: removedTracks));
        return WithFailure(_playlistEdits.RemoveRowsAsync(playlistUri, rows, ct), id);
    }

    public Task MovePlaylistRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, int toIndex, CancellationToken ct = default)
    {
        long id = -1;
        if (!_activity.IsSuppressed && rows.Count > 0)
        {
            var refs = new ActivityTrackRef[rows.Count];
            for (int i = 0; i < rows.Count; i++) refs[i] = new ActivityTrackRef(rows[i].Uri, null, rows[i].ItemId);
            id = _activity.Record(ActivityKind.PlaylistMoveTracks, playlistUri, null,
                new ActivityPayload(Tracks: refs, FromIndex: rows[0].Index, ToIndex: toIndex));
        }
        return WithFailure(_playlistEdits.MoveRowsAsync(playlistUri, rows, toIndex, ct), id);
    }

    public Task<string> CreateContributorInviteAsync(string playlistUri, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.ContributorInvite, playlistUri);   // log-only
        return WithFailure(_playlistEdits.CreateContributorInviteAsync(playlistUri, ct), id);
    }

    public Task<PlaylistBasePermission?> GetPlaylistBasePermissionAsync(string playlistUri, CancellationToken ct = default)
        => _playlistEdits.GetBasePermissionAsync(playlistUri, ct);

    public Task SetPlaylistVisibilityAsync(string playlistUri, bool isPublic, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.PlaylistVisibility, playlistUri, null, new ActivityPayload(NewIsPublic: isPublic));
        return WithFailure(_playlistEdits.SetPlaylistVisibilityAsync(playlistUri, isPublic, ct), id);
    }

    public Task DeletePlaylistAsync(string playlistUri, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.PlaylistDelete, playlistUri);   // log-only, destructive
        return WithFailure(_playlistEdits.DeletePlaylistAsync(playlistUri, ct), id);
    }

    // ── activity plumbing ──────────────────────────────────────────────────────────────────────────────────
    // Optimistic Done; an IMMEDIATE Task fault flips the entry to Failed. Eventual outbox dead-letter failures are NOT
    // reflected (documented cut). The continuation runs off the UI thread — ActivityLog is thread-safe.
    Task WithFailure(Task task, long id)
    {
        if (id >= 0) _ = task.ContinueWith(t => { if (t.IsFaulted) _activity.MarkFailed(id); }, TaskScheduler.Default);
        return task;
    }

    Task<T> WithFailure<T>(Task<T> task, long id)
    {
        if (id >= 0) _ = task.ContinueWith(t => { if (t.IsFaulted) _activity.MarkFailed(id); }, TaskScheduler.Default);
        return task;
    }

    static ActivityTrackRef TrackRef(Track t) => new(t.Uri, t.Title, t.ContextUid);

    static ActivityPayload PayloadFor(IReadOnlyList<Track> tracks)
    {
        var refs = new ActivityTrackRef[tracks.Count];
        for (int i = 0; i < tracks.Count; i++) refs[i] = TrackRef(tracks[i]);
        return new ActivityPayload(Tracks: refs);
    }
}
