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
        return n;
    }

    public static int AddToEnd(IPlaybackPlayer? player, IReadOnlyList<Track> tracks, int max = MaxBatch)
    {
        if (player is null) return 0;
        int n = Count(tracks, max);
        for (int i = 0; i < n; i++) _ = player.EnqueueAsync(tracks[i].Uri);
        return n;
    }

    public static PlaybackContextTrack[] ToPlaybackContextTracks(IReadOnlyList<Track> tracks, int count)
    {
        var ordered = new PlaybackContextTrack[count];
        for (int i = 0; i < count; i++)
            ordered[i] = new PlaybackContextTrack(tracks[i].Uri, tracks[i].ContextUid ?? string.Empty);
        return ordered;
    }

    static int Count(IReadOnlyList<Track> tracks, int max) => System.Math.Min(tracks.Count, System.Math.Max(0, max));
}
