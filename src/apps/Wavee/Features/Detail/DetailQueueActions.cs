using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

static class DetailQueueActions
{
    public const int MaxBatch = 50;

    public static int PlayNext(IPlaybackPlayer? player, IReadOnlyList<Track> tracks, int max = MaxBatch)
    {
        if (player is null) return 0;
        int n = Count(tracks, max);
        if (n <= 0) return 0;
        _ = player.PlayNextAsync(ToPlaybackContextTracks(tracks, n));
        // Queueing only works when a remote device is playing (local playback is unsupported). With none active the call
        // was rejected → the standard "choose a remote device" toast fired; return 0 so the caller shows no "added" toast.
        return RemoteActive(player) ? n : 0;
    }

    public static int AddToEnd(IPlaybackPlayer? player, IReadOnlyList<Track> tracks, int max = MaxBatch)
    {
        if (player is null) return 0;
        int n = Count(tracks, max);
        if (n <= 0) return 0;
        if (!RemoteActive(player)) return 0;   // defense-in-depth: queueing is remote-only; the caller (DetailShell) already guards + prompts. No "added" toast.
        for (int i = 0; i < n; i++) _ = player.EnqueueAsync(tracks[i]);
        return n;
    }

    // A remote Connect device is the active playback target. Since local playback is unsupported, an active device is
    // always a remote one — so a non-empty ActiveDeviceId means enqueue/play-next will actually forward and succeed.
    static bool RemoteActive(IPlaybackPlayer player) => !string.IsNullOrEmpty(player.State.ActiveDeviceId);

    public static PlaybackContextTrack[] ToPlaybackContextTracks(IReadOnlyList<Track> tracks, int count)
    {
        var ordered = new PlaybackContextTrack[count];
        for (int i = 0; i < count; i++)
        {
            var t = tracks[i];
            ordered[i] = new PlaybackContextTrack(t.Uri, t.ContextUid ?? string.Empty, BuildMetadata(t));
        }
        return ordered;
    }

    // The per-track display metadata the target device shows for an inserted "play next" row (set_queue). Mirrors the
    // desktop capture's metadata map (title/artist/album/duration/image/explicit/player). is_queued is NOT set here — the
    // wire serializer (OutboundEnvelope.WriteQueueEntry) stamps is_queued:"true" onto queued rows.
    static IReadOnlyDictionary<string, string> BuildMetadata(Track t)
    {
        var m = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["title"] = t.Title ?? "",
            ["album_title"] = t.Album?.Name ?? "",
            ["album_uri"] = t.Album?.Uri ?? "",
            ["duration"] = t.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["image_url"] = t.Image?.Url ?? "",
            ["is_explicit"] = t.IsExplicit ? "true" : "false",
            ["track_player"] = "audio",
        };
        if (t.Artists.Count > 0)
        {
            var a = t.Artists[0];
            m["artist_name"] = a.Name ?? "";
            m["artist_uri"] = a.Uri ?? "";
            m["album_artist_name"] = a.Name ?? "";
        }
        return m;
    }

    static int Count(IReadOnlyList<Track> tracks, int max) => System.Math.Min(tracks.Count, System.Math.Max(0, max));
}
