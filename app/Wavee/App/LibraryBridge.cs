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
public sealed class LibraryBridge
{
    public static readonly Context<LibraryBridge?> Slot = new(null);

    readonly IMutationSource _mut;
    readonly UserPlaylistSource _playlists;
    readonly IPlaylistMutationSource _playlistEdits;
    readonly List<IDisposable> _subs = [];
    bool _active;

    /// <summary>The saved-set — read by the heart / follow affordances (which subscribe → re-skin on any change).</summary>
    public Signal<IReadOnlySet<string>> Saved { get; }
    /// <summary>Bumps whenever a user playlist is created / added-to — the sidebar keys its playlist read on it to refresh.</summary>
    public Signal<int> PlaylistsVersion { get; } = new(0);

    public LibraryBridge(IMutationSource mut, UserPlaylistSource playlists, IPlaylistMutationSource playlistEdits)
    {
        _mut = mut;
        _playlists = playlists;
        _playlistEdits = playlistEdits;
        Saved = new Signal<IReadOnlySet<string>>(mut.Saved);
    }

    /// <summary>Subscribe the source's change stream → the signal. Idempotent. Call once from a mount effect with <c>UsePost()</c>.</summary>
    public void Activate(Action<Action> post)
    {
        if (_active) return;
        _active = true;
        _subs.Add(_mut.SavedChanged.Subscribe(s => post(() => Saved.Value = s)));
        _subs.Add(_playlists.PlaylistsChanged.Subscribe(v => post(() => PlaylistsVersion.Value = v)));
    }

    // ── playlist edits (create + add) ──────────────────────────────────────────────────────────────────────
    public string CreatePlaylist(string name) => _playlists.CreatePlaylist(name);
    public void AddToPlaylist(string playlistUri, Track track) => _playlists.AddTrack(playlistUri, track);

    /// <summary>Add tracks to ANY editable playlist by uri — the "Copy to playlist" picker's target. The mutation seam
    /// routes <c>wavee:playlist:*</c> to the local source and <c>spotify:playlist:*</c> to the real Spotify path; it
    /// fails loud (never silently no-ops) if a real backend isn't wired, which is intended.</summary>
    public Task AddTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, CancellationToken ct = default)
        => _playlistEdits.AddTracksAsync(playlistUri, tracks, ct);

    /// <summary>Add tracks to the user's default playlist (creating one if none) — the no-picker "Add to playlist".
    /// Returns the target (uri, name) for a confirmation toast.</summary>
    public (string Uri, string Name) AddToDefaultPlaylist(IEnumerable<Track> tracks)
    {
        var target = _playlists.DefaultPlaylist();
        foreach (var t in tracks) _playlists.AddTrack(target.Uri, t);
        return target;
    }

    /// <summary>Is this uri saved / liked / followed? Reads the signal → subscribes the caller (live heart state).</summary>
    public bool IsSaved(string uri) => Saved.Value.Contains(uri);

    /// <summary>Toggle saved-state with an OPTIMISTIC local flip (the heart updates this frame), then reconcile through
    /// the source (which re-emits the confirmed set). Called from a click handler, so the reads here don't subscribe.</summary>
    public void ToggleSaved(string uri) => SetSaved(uri, !Saved.Peek().Contains(uri));

    public void SetSaved(string uri, bool saved)
    {
        var cur = Saved.Peek();
        if (cur.Contains(uri) == saved) return;
        var next = new HashSet<string>(cur);
        if (saved) next.Add(uri); else next.Remove(uri);
        Saved.Value = next;                      // optimistic
        _ = _mut.SetSavedAsync(uri, saved);      // reconcile (re-emits the confirmed set via the bridge subscription)
    }

    // ── Spotify playlist editing ─────────────────────────────────────────────────────────────────────────
    public Task UpdatePlaylistDetailsAsync(string playlistUri, string? name, string? description, bool? collaborative, CancellationToken ct = default)
        => _playlistEdits.UpdateDetailsAsync(playlistUri, name, description, collaborative, ct);

    public Task SetPlaylistCoverJpegAsync(string playlistUri, byte[] jpeg, CancellationToken ct = default)
        => _playlistEdits.SetCoverJpegAsync(playlistUri, jpeg, ct);

    public Task ClearPlaylistCoverAsync(string playlistUri, CancellationToken ct = default)
        => _playlistEdits.ClearCoverAsync(playlistUri, ct);

    public Task RemovePlaylistRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, CancellationToken ct = default)
        => _playlistEdits.RemoveRowsAsync(playlistUri, rows, ct);

    public Task MovePlaylistRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, int toIndex, CancellationToken ct = default)
        => _playlistEdits.MoveRowsAsync(playlistUri, rows, toIndex, ct);

    public Task<string> CreateContributorInviteAsync(string playlistUri, CancellationToken ct = default)
        => _playlistEdits.CreateContributorInviteAsync(playlistUri, ct);

    public Task<PlaylistBasePermission?> GetPlaylistBasePermissionAsync(string playlistUri, CancellationToken ct = default)
        => _playlistEdits.GetBasePermissionAsync(playlistUri, ct);

    public Task SetPlaylistVisibilityAsync(string playlistUri, bool isPublic, CancellationToken ct = default)
        => _playlistEdits.SetPlaylistVisibilityAsync(playlistUri, isPublic, ct);

    public Task DeletePlaylistAsync(string playlistUri, CancellationToken ct = default)
        => _playlistEdits.DeletePlaylistAsync(playlistUri, ct);
}
