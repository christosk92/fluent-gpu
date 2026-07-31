using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using Wavee.Core;

namespace Wavee;

/// <summary>The one in-app drag discriminator for navigable resources and playable items. Targets decide capability
/// from <see cref="WaveeResourceDragPayload.Kind"/> instead of inventing surface-specific discriminators.</summary>
static class WaveeDragKinds
{
    public const string Resource = "wavee.resource";
}

enum WaveeResourceKind : byte
{
    Route, Playlist, Album, Artist, Show, Folder, Track, Episode,
}

/// <summary>A transport envelope shared by tabs, sidebar rows and track lists. Display/navigation identity is retained
/// separately from an optional ordered track snapshot; source playlist row refs make a same-playlist drop a move while
/// every other playlist target is a copy. The resolver is cold and runs only after a compatible drop.</summary>
sealed record WaveeResourceDragPayload(
    WaveeResourceKind Kind,
    string Id,
    string Uri,
    string Name,
    IReadOnlyList<Track>? Tracks = null,
    string? SourcePlaylistUri = null,
    IReadOnlyList<PlaylistRowRef>? SourceRows = null,
    Func<CancellationToken, Task<IReadOnlyList<Track>>>? TrackResolver = null,
    bool RootlistItem = false)
{
    public bool CanPin => Kind is WaveeResourceKind.Route or WaveeResourceKind.Playlist or WaveeResourceKind.Album
        or WaveeResourceKind.Artist or WaveeResourceKind.Show or WaveeResourceKind.Folder;

    public bool CanCopyTracks => Tracks is { Count: > 0 } || TrackResolver is not null;

    public Task<IReadOnlyList<Track>> ResolveTracksAsync(CancellationToken ct = default)
        => Tracks is { } tracks ? Task.FromResult(tracks)
            : TrackResolver is { } resolve ? resolve(ct)
            : Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>());

    public bool TryPin(out SidebarPinKind kind)
    {
        kind = Kind switch
        {
            WaveeResourceKind.Playlist => SidebarPinKind.Playlist,
            WaveeResourceKind.Album => SidebarPinKind.Album,
            WaveeResourceKind.Artist => SidebarPinKind.Artist,
            WaveeResourceKind.Show => SidebarPinKind.Show,
            WaveeResourceKind.Folder => SidebarPinKind.Folder,
            WaveeResourceKind.Route => SidebarPinKind.Route,
            _ => SidebarPinKind.Route,
        };
        return CanPin;
    }

    public static WaveeResourceDragPayload FromEntry(SidebarLibraryEntry entry, Services? svc, bool rootlistItem = false)
    {
        var kind = entry.Kind switch
        {
            SidebarEntryKind.Playlist => WaveeResourceKind.Playlist,
            SidebarEntryKind.Album => WaveeResourceKind.Album,
            SidebarEntryKind.Artist => WaveeResourceKind.Artist,
            SidebarEntryKind.Show => WaveeResourceKind.Show,
            SidebarEntryKind.Folder => WaveeResourceKind.Folder,
            SidebarEntryKind.Track => WaveeResourceKind.Track,
            _ => WaveeResourceKind.Route,
        };
        return new(kind, entry.Id, entry.Uri, entry.Name,
            TrackResolver: ResolverFor(kind, entry.Uri, svc), RootlistItem: rootlistItem);
    }

    public static WaveeResourceDragPayload FromDestination(SidebarDestination destination, Services? svc)
    {
        var kind = destination.Kind switch
        {
            SidebarPinKind.Playlist => WaveeResourceKind.Playlist,
            SidebarPinKind.Album => WaveeResourceKind.Album,
            SidebarPinKind.Artist => WaveeResourceKind.Artist,
            SidebarPinKind.Show => WaveeResourceKind.Show,
            SidebarPinKind.Folder => WaveeResourceKind.Folder,
            _ => WaveeResourceKind.Route,
        };
        return new(kind, destination.PinId, destination.Uri, destination.Name,
            TrackResolver: ResolverFor(kind, destination.Uri, svc));
    }

    static Func<CancellationToken, Task<IReadOnlyList<Track>>>? ResolverFor(
        WaveeResourceKind kind, string uri, Services? svc)
    {
        if (svc is null || uri.Length == 0) return null;
        if (kind == WaveeResourceKind.Playlist)
            return async ct => (await svc.Library.GetPlaylistAsync(uri, ct).ConfigureAwait(false))?.Tracks
                ?? Array.Empty<Track>();
        if (kind == WaveeResourceKind.Album)
            return async ct => (await svc.Library.GetAlbumAsync(uri, ct).ConfigureAwait(false))?.Tracks
                ?? Array.Empty<Track>();
        return null;
    }
}

static class WaveeResourceDrag
{
    /// <summary>Unwrap either a plain source or an item owned by <see cref="Reorderable"/>.</summary>
    public static WaveeResourceDragPayload? Unwrap(object? payload) => payload switch
    {
        WaveeResourceDragPayload direct => direct,
        ReorderPayload { Item: WaveeResourceDragPayload wrapped } => wrapped,
        _ => null,
    };
}

static class WaveeResourceDrop
{
    public static bool CanDepositTracks(object? payload)
        => WaveeResourceDrag.Unwrap(payload) is { CanCopyTracks: true };

    public static bool CanMoveRootlist(object? payload)
        => WaveeResourceDrag.Unwrap(payload) is { RootlistItem: true,
            Kind: WaveeResourceKind.Playlist or WaveeResourceKind.Folder };

    public static void MoveRootlist(ActionServices acts, object? payload,
                                    WaveeResourceDragPayload target, RootlistDropPlacement placement)
    {
        if (acts.Library is not { } lib || WaveeResourceDrag.Unwrap(payload) is not { RootlistItem: true } source) return;
        var sourceRef = RootRef(source);
        var targetRef = RootRef(target);
        if (sourceRef.Key.Length == 0 || targetRef.Key.Length == 0) return;
        _ = Run();

        async Task Run()
        {
            try { await lib.MoveRootlistItemAsync(sourceRef, targetRef, placement).ConfigureAwait(false); }
            catch (Exception ex) { acts.Post?.Invoke(() => PlaylistEditErrors.Toast(ex)); }
        }
    }

    static RootlistItemRef RootRef(WaveeResourceDragPayload payload)
        => payload.Kind == WaveeResourceKind.Folder
            ? new RootlistItemRef(SidebarPinId.FolderIdOf(payload.Id), IsFolder: true)
            : new RootlistItemRef(payload.Uri, IsFolder: false);

    /// <summary>Commit one playlist deposit. Same-playlist row drags move stable membership rows; every other resource
    /// resolves to an ordered track snapshot and copies it. A null insertion index means append (sidebar target).</summary>
    public static void DepositTracks(ActionServices acts, string targetUri, string targetName,
                                     object? payload, int? insertionIndex)
    {
        if (acts.Library is not { } lib || WaveeResourceDrag.Unwrap(payload) is not { } resource) return;
        if (!resource.CanCopyTracks) return;
        _ = Run();

        async Task Run()
        {
            try
            {
                bool moved = false;
                if (insertionIndex is { } at
                    && string.Equals(resource.SourcePlaylistUri, targetUri, StringComparison.Ordinal)
                    && resource.SourceRows is { Count: > 0 } rows)
                {
                    await lib.MovePlaylistRowsAsync(targetUri, rows, at).ConfigureAwait(false);
                    moved = true;
                }
                else
                {
                    // Dropping a playlist onto itself without membership refs would append a duplicate of the entire
                    // playlist. Treat that ambiguous container-on-itself gesture as a no-op; track-row drags still move.
                    if (resource.Kind == WaveeResourceKind.Playlist
                        && string.Equals(resource.Uri, targetUri, StringComparison.Ordinal)) return;
                    var tracks = await resource.ResolveTracksAsync().ConfigureAwait(false);
                    if (tracks.Count == 0) return;
                    if (insertionIndex is { } insert)
                        await lib.InsertTracksAsync(targetUri, tracks, insert).ConfigureAwait(false);
                    else
                        await lib.AddTracksAsync(targetUri, tracks).ConfigureAwait(false);
                }

                if (!moved)
                    acts.Post?.Invoke(() => Toast.Show(Strings.Detail.AddedToPlaylist(targetName),
                        new ToastOptions { Severity = InfoBarSeverity.Success }));
            }
            catch (Exception ex)
            {
                acts.Post?.Invoke(() => PlaylistEditErrors.Toast(ex));
            }
        }
    }
}
