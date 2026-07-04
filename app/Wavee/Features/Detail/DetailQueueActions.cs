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
        if (!RemoteActive(player)) { _ = player.EnqueueAsync(tracks[0].Uri); return 0; }   // trigger the "choose a device" toast once; no "added" toast
        for (int i = 0; i < n; i++) _ = player.EnqueueAsync(tracks[i].Uri);
        return n;
    }

    // A remote Connect device is the active playback target. Since local playback is unsupported, an active device is
    // always a remote one — so a non-empty ActiveDeviceId means enqueue/play-next will actually forward and succeed.
    static bool RemoteActive(IPlaybackPlayer player) => !string.IsNullOrEmpty(player.State.ActiveDeviceId);

    public static PlaybackContextTrack[] ToPlaybackContextTracks(IReadOnlyList<Track> tracks, int count)
    {
        var ordered = new PlaybackContextTrack[count];
        for (int i = 0; i < count; i++)
            ordered[i] = new PlaybackContextTrack(tracks[i].Uri, tracks[i].ContextUid ?? string.Empty);
        return ordered;
    }

    static int Count(IReadOnlyList<Track> tracks, int max) => System.Math.Min(tracks.Count, System.Math.Max(0, max));
}
