using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

// The pure decision core behind the AppAction enablement / checked predicates — extracted so the rules are unit-testable
// engine-free (Wavee.Tests source-includes this file). The AppAction lambdas (Actions/TrackActions.cs etc.) are thin
// adapters over these: services in, rule here.
public static class ActionRules
{
    /// <summary>ToggleLike checked-state: checked iff EVERY track (≥1) is saved. A track without a uri counts unsaved.</summary>
    public static bool AllSaved(IReadOnlyList<Track> tracks, Func<string, bool> isSaved)
    {
        if (tracks is not { Count: > 0 }) return false;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (t.Uri.Length == 0 || !isSaved(t.Uri)) return false;
        }
        return true;
    }

    /// <summary>View-credits gate: a single track carrying a primary artist uri (the NPV fetch keys off artistUri +
    /// trackUri, so both must be present).</summary>
    public static bool CanViewCredits(in ActionTarget target)
        => target.Single is { Uri.Length: > 0, Artists: { Count: > 0 } artists } && artists[0].Uri.Length > 0;

    /// <summary>Song-radio gate: exactly one track carrying a <c>spotify:track:</c> uri (a player-present check rides at
    /// the action). Radio seeds a single track — a multi-select or non-track uri is disabled.</summary>
    public static bool CanStartTrackRadio(in ActionTarget target)
        => target.Single is { Uri.Length: > 0 } t && t.Uri.StartsWith("spotify:track:", StringComparison.Ordinal);

    /// <summary>Artist-radio gate: an Artist container target carrying a <c>spotify:artist:</c> uri.</summary>
    public static bool CanStartArtistRadio(in ActionTarget target)
        => target.Kind == TargetKind.Artist && target.Uri is { Length: > 0 } uri
           && uri.StartsWith("spotify:artist:", StringComparison.Ordinal);

    /// <summary>Remove-from-this-playlist gate: an editable host with resolved rows.</summary>
    public static bool CanRemoveFromPlaylist(PlaylistHost? host) =>
        host is { Caps.CanEditItems: true, Rows.Count: > 0 };

    /// <summary>The route key <c>go(key, name)</c> takes for a container target (the app's nav scheme:
    /// <c>album:</c> / <c>artist:</c> / <c>pl:</c> / <c>liked</c>). Null = not navigable.</summary>
    public static string? RouteFor(in ActionTarget target)
    {
        if (target.Uri is not { Length: > 0 } uri) return null;
        if (uri == "spotify:collection:tracks") return "liked";
        return target.Kind switch
        {
            TargetKind.Album => "album:" + uri,
            TargetKind.Artist => "artist:" + uri,
            TargetKind.Playlist or TargetKind.SidebarItem => "pl:" + uri,
            _ => null,
        };
    }
}
