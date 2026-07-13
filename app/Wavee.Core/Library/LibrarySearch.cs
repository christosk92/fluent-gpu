using System;
using System.Collections.Generic;

namespace Wavee.Core;

/// <summary>Which slice of the cached library a full-text search covers — mirrors the library page's kind. Artists →
/// followed artists ▸ their albums ▸ tracks in those albums. Albums → saved albums ▸ tracks in those albums.</summary>
public enum LibrarySearchScope { Artists, Albums }

/// <summary>One matching track inside an album group. <see cref="AlbumIndex"/> is its position in the album's full
/// tracklist (for playback). <see cref="MatchLen"/> == 0 means the title itself didn't match — the track is shown
/// because its album/artist matched — so no highlight is drawn.</summary>
public readonly record struct LibraryTrackHit(
    string Uri, string Title, Image? Cover, int AlbumIndex, int MatchStart, int MatchLen);

/// <summary>One matching album, grouped under its artist. Carries its matching tracks (all tracks when the album/artist
/// name matched; only the matching tracks otherwise). <see cref="MatchLen"/> == 0 → the album name didn't match (it's
/// here because a track/artist matched) → no highlight.</summary>
public sealed record LibraryAlbumGroup(
    string Uri, string Name, Image? Cover, int Year, AlbumKind Kind,
    int MatchStart, int MatchLen, IReadOnlyList<LibraryTrackHit> Tracks);

/// <summary>One matching artist and their matching albums. Present when the artist name matched OR any album/track under
/// them matched. <see cref="MatchLen"/> == 0 → the name didn't match (a child did) → no highlight.</summary>
public sealed record LibraryArtistGroup(
    string Uri, string Name, Image? Image,
    int MatchStart, int MatchLen, IReadOnlyList<LibraryAlbumGroup> Albums);

/// <summary>The hierarchical library-search result. Artists-scope populates <see cref="Artists"/> (artist ▸ album ▸
/// track drill-down); albums-scope populates <see cref="Albums"/> (album ▸ track). The UI fans these across the
/// master-detail columns and filters+highlights each level.</summary>
public sealed record LibrarySearchResults(
    IReadOnlyList<LibraryArtistGroup> Artists,
    IReadOnlyList<LibraryAlbumGroup> Albums)
{
    public static readonly LibrarySearchResults Empty =
        new(Array.Empty<LibraryArtistGroup>(), Array.Empty<LibraryAlbumGroup>());

    public bool IsEmpty => Artists.Count == 0 && Albums.Count == 0;
}
