using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// FIXTURE-A (cluster-complex-queue.json) — full session restore semantics (§8, §12).
public class QueueRecoveryTests
{
    static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    static ClusterDelta LoadFixtureA()
    {
        string path = FixturePath("cluster-complex-queue.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var cluster = doc.RootElement.GetProperty("payloads")[0].GetProperty("cluster");
        var ps = cluster.GetProperty("player_state");
        var trackEl = ps.GetProperty("track");
        var track = MapRemote(trackEl, long.Parse(ps.GetProperty("duration").GetString() ?? "0"));

        var next = new List<RemoteTrack>();
        foreach (var t in ps.GetProperty("next_tracks").EnumerateArray())
            next.Add(MapRemote(t, 0));

        var prev = new List<RemoteTrack>();
        foreach (var t in ps.GetProperty("prev_tracks").EnumerateArray())
            prev.Add(MapRemote(t, 0));

        var opts = ps.GetProperty("options");
        bool shuffle = opts.TryGetProperty("shuffling_context", out var sh) && sh.GetBoolean();
        bool repTrack = opts.TryGetProperty("repeating_track", out var rt) && rt.GetBoolean();
        bool repCtx = opts.TryGetProperty("repeating_context", out var rc) && rc.GetBoolean();
        var repeat = repTrack ? RepeatMode.Track : repCtx ? RepeatMode.Context : RepeatMode.Off;

        return new ClusterDelta(
            cluster.GetProperty("active_device_id").GetString() ?? "",
            true, track,
            ps.GetProperty("context_uri").GetString(),
            ps.GetProperty("is_playing").GetBoolean(),
            ps.GetProperty("is_paused").GetBoolean(),
            false,
            long.Parse(ps.GetProperty("position_as_of_timestamp").GetString() ?? "0"),
            long.Parse(ps.GetProperty("timestamp").GetString() ?? "0"),
            long.Parse(cluster.GetProperty("server_timestamp_ms").GetString() ?? "0"),
            long.Parse(ps.GetProperty("duration").GetString() ?? "0"),
            shuffle, repeat,
            Array.Empty<ConnectDeviceRow>(),
            next,
            QueueRevision: ps.GetProperty("queue_revision").GetString() ?? "",
            PrevTracks: prev);
    }

    static RemoteTrack MapRemote(JsonElement t, long fallbackDur)
    {
        string uri = t.GetProperty("uri").GetString() ?? "";
        string uid = t.TryGetProperty("uid", out var u) ? u.GetString() ?? "" : "";
        string provider = t.TryGetProperty("provider", out var p) ? p.GetString() ?? "" : "";
        Dictionary<string, string>? meta = null;
        if (t.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            meta = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in m.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    meta[prop.Name] = prop.Value.GetString() ?? "";
        }
        long dur = meta is not null && meta.TryGetValue("duration", out var ds) && long.TryParse(ds, out var d) ? d : fallbackDur;
        string title = meta?.GetValueOrDefault("title") ?? uri;
        return new RemoteTrack(uri, title, meta?.GetValueOrDefault("artist_name") ?? "",
            meta?.GetValueOrDefault("artist_uri") ?? "", meta?.GetValueOrDefault("album_title") ?? "",
            meta?.GetValueOrDefault("album_uri") ?? "", null, dur, uid, provider, meta);
    }

    static Track Hydrate(RemoteTrack r) =>
        new(r.Uri[(r.Uri.LastIndexOf(':') + 1)..], r.Uri, r.Title,
            Array.Empty<ArtistRef>(), new AlbumRef("", r.AlbumUri, r.AlbumName), r.DurationMs, false, null);

    [Fact]
    public void ReplaceFromCluster_FixtureA_MatchesSemantics()
    {
        var delta = LoadFixtureA();
        var session = new PlaybackSession();
        var snap = session.ReplaceFromCluster(delta, Hydrate(delta.Track));

        Assert.Equal("spotify:track:3ySSbGT5BepfePnva86js7", snap.Current?.Track.Uri);
        Assert.Equal(QueueProvider.Queue, snap.Current?.Provider);
        Assert.Equal("q0", snap.Current?.Uid);
        Assert.Equal("12613583692104578720", snap.ClusterQueueRevision);

        Assert.Equal(new[] { "q3", "q1", "q4", "q5", "q2" },
            snap.UserQueue.Select(e => e.Uid).ToArray());

        Assert.Empty(snap.History);   // local-only: cluster prev_tracks are not imported

        var upUris = snap.Upcoming.Where(e => e.Provider == QueueProvider.Context).Select(e => e.Track.Uri).ToArray();
        Assert.Equal(new[]
        {
            "spotify:track:4HkHeiG61K2fmftXVpbUyu",
            "spotify:track:1vvw7JovumZ290gftoLPdF",
            "spotify:track:285hMzLhJwHVLe9QT9qilk",   // jam.patch row surfaced as context
        }, upUris);

        Assert.Equal("spotify:station:album:0apIvboeRy3QYd13K5Dfj4", snap.AutoplayContextUri);
        Assert.Contains(snap.Upcoming, e => e.Provider == QueueProvider.Autoplay
            && e.Track.Uri == "spotify:track:1bhLix8ZApePHFLiVCKpp4");

        Assert.DoesNotContain(snap.Upcoming, e => e.Track.Uri == "spotify:meta:page:1");
        Assert.DoesNotContain(snap.Upcoming, e => e.Track.Uri == "spotify:delimiter");
    }
}
