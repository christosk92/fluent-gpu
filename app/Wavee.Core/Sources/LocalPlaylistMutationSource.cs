using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

/// <summary>Fake-backend playlist mutations: local <c>wavee:playlist:*</c> add only; Spotify edits fail loud.</summary>
public sealed class LocalPlaylistMutationSource : IPlaylistMutationSource
{
    readonly UserPlaylistSource _local;

    public LocalPlaylistMutationSource(UserPlaylistSource local) => _local = local;

    public Task AddTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, CancellationToken ct = default)
    {
        RequireLocal(playlistUri);
        foreach (var t in tracks) _local.AddTrack(playlistUri, t);
        return Task.CompletedTask;
    }

    public Task RemoveRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, CancellationToken ct = default)
    {
        RequireLocal(playlistUri);
        throw NotImplementedLocally("row removal", playlistUri);
    }

    public Task MoveRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, int toIndex, CancellationToken ct = default)
    {
        RequireLocal(playlistUri);
        throw NotImplementedLocally("row reordering", playlistUri);
    }

    public Task UpdateDetailsAsync(string playlistUri, string? name, string? description, bool? collaborative, CancellationToken ct = default)
    {
        RequireLocal(playlistUri);
        throw NotImplementedLocally("metadata editing", playlistUri);
    }

    public Task SetCoverJpegAsync(string playlistUri, byte[] jpeg, CancellationToken ct = default)
    {
        RequireLocal(playlistUri);
        throw NotImplementedLocally("cover editing", playlistUri);
    }

    public Task ClearCoverAsync(string playlistUri, CancellationToken ct = default)
    {
        RequireLocal(playlistUri);
        throw NotImplementedLocally("cover editing", playlistUri);
    }

    public Task SetBasePermissionAsync(string playlistUri, PlaylistPermissionLevel level, CancellationToken ct = default)
        => throw SpotifyOnly(playlistUri);

    public Task<PlaylistBasePermission?> GetBasePermissionAsync(string playlistUri, CancellationToken ct = default)
        => Task.FromResult<PlaylistBasePermission?>(null);

    public Task SetPlaylistVisibilityAsync(string playlistUri, bool isPublic, CancellationToken ct = default)
        => throw SpotifyOnly(playlistUri);

    public Task DeletePlaylistAsync(string playlistUri, CancellationToken ct = default)
        => throw SpotifyOnly(playlistUri);

    public Task<string> CreateContributorInviteAsync(string playlistUri, CancellationToken ct = default)
        => throw SpotifyOnly(playlistUri);

    static void RequireLocal(string uri)
    {
        if (!uri.StartsWith("wavee:playlist:", StringComparison.Ordinal))
            throw SpotifyOnly(uri);
    }

    static NotSupportedException SpotifyOnly(string uri) =>
        new($"Spotify playlist editing is not available offline (uri={uri}). Sign in with the real backend.");

    static NotSupportedException NotImplementedLocally(string what, string uri) =>
        new($"Local playlist {what} is not implemented (uri={uri}).");
}
