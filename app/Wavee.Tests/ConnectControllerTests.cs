using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Stage E — command arbitration: the routing spine (local iff nobody/we active, else forward), ghost resume, per-verb
// routing, "another device active stops local", transfer self/away, and inbound-always-local. See
// docs/plans/wavee-playback-arbitration-rules.md.
public class ConnectControllerTests
{
    static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    static IContextResolver Ctx(params string[] uris) => new FakeContextResolver(uris);

    sealed class RecordingAudioHost : IAudioHost
    {
        public readonly List<string> Calls = new();
        readonly SimpleSubject<AudioHostSignal> _sig = new();
        public IObservable<AudioHostSignal> Signals => _sig;
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsBuffering => false;
        public void Load(in AudioStreamHandle s) { Calls.Add("load:" + s.TrackUri); }
        public void LoadFastStart(in AudioFastStart s) { Calls.Add("faststart:" + s.TrackUri); }
        public void SupplyBody(in AudioStreamHandle s) { Calls.Add("body:" + s.TrackUri); }
        public void Play() { IsPlaying = true; Calls.Add("play"); }
        public void Pause() { IsPlaying = false; Calls.Add("pause"); }
        public void Stop() { IsPlaying = false; Calls.Add("stop"); }
        public void Seek(long ms) { PositionMs = ms; Calls.Add("seek:" + ms); }
        public void SetVolume(double v) { Calls.Add("vol"); }
        public void Emit(AudioHostSignal s) => _sig.OnNext(s);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class RecordingOutbound : IOutboundControl
    {
        public readonly List<(string Target, string Json)> Sent = new();
        public readonly List<(string Target, int Volume)> Volumes = new();
        public readonly List<(string From, string Target)> Transfers = new();
        public bool TransferOk { get; set; } = true;
        public string? LastTarget => Sent.Count > 0 ? Sent[^1].Target : null;
        public string? LastJson => Sent.Count > 0 ? Sent[^1].Json : null;
        public int? LastVolume => Volumes.Count > 0 ? Volumes[^1].Volume : null;
        public Task<OutboundResult> SendAsync(string targetDeviceId, string commandJson, CancellationToken ct = default)
        { Sent.Add((targetDeviceId, commandJson)); return Task.FromResult(new OutboundResult(true, "ack-test", 200)); }
        public Task<OutboundResult> SetVolumeAsync(string targetDeviceId, int volume0_65535, CancellationToken ct = default)
        { Volumes.Add((targetDeviceId, volume0_65535)); return Task.FromResult(new OutboundResult(true, "ack-test", 200)); }
        public Task<OutboundResult> TransferAsync(string fromDeviceId, string targetDeviceId, CancellationToken ct = default)
        {
            Transfers.Add((fromDeviceId, targetDeviceId));
            return Task.FromResult(new OutboundResult(TransferOk, TransferOk ? "ack-test" : null, TransferOk ? 200 : 500));
        }
    }

    sealed class RecordingProjection : IPlaybackProjection
    {
        public readonly List<PlaybackEvent> Events = new();
        public void OnEvent(in PlaybackEvent e) => Events.Add(e);
        public int Count(EvKind kind) => Events.Count(e => e.Kind == kind);
    }

    static ClusterDelta Cluster(string active, RemoteTrack? track = null, long pos = 0, bool playing = false) =>
        new(active, track is not null, track ?? default, "spotify:playlist:ctx",
            playing, !playing, false, pos, 0, 0, track?.DurationMs ?? 0, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

    static RemoteTrack Remote(string uri, long dur = 200000) => new(uri, "G", "A", "spotify:artist:a", "Al", "spotify:album:al", null, dur);

    PlaybackController Make(out RecordingAudioHost host, out NowPlayingProjection proj, out RecordingOutbound outbound,
        IContextResolver? ctx = null, Func<long>? clock = null, IReadOnlyList<IPlaybackProjection>? extra = null)
    {
        host = new RecordingAudioHost();
        proj = new NowPlayingProjection("us", clock ?? (() => 0));
        outbound = new RecordingOutbound();
        return new PlaybackController(host, new StubTrackResolver(), proj, ctx ?? Ctx("spotify:track:a", "spotify:track:b"), "us", outbound, extra);
    }

    [Fact]
    public async Task NoActiveDevice_Play_RoutesLocal()
    {
        using var c = Make(out var host, out _, out var outbound);
        await c.PlayAsync("spotify:playlist:p");
        Assert.Contains("load:spotify:track:a", host.Calls);
        Assert.Contains("play", host.Calls);
        Assert.Empty(outbound.Sent);
    }

    [Fact]
    public async Task PlayTrack_WithKnownTrack_PublishesMetadataImmediately()
    {
        using var c = Make(out var host, out var proj, out var outbound, ctx: EmptyContextResolver.Instance);
        var track = new Track("known", "spotify:track:known", "Known Title",
            [new ArtistRef("artist", "spotify:artist:artist", "Known Artist")],
            new AlbumRef("album", "spotify:album:album", "Known Album"), 123000, false,
            new Image("https://i.scdn.co/image/known", 300, 300));

        await c.PlayTrackAsync(track);

        Assert.Contains("load:spotify:track:known", host.Calls);
        Assert.Equal("Known Title", proj.CurrentTrack?.Title);
        Assert.Equal("Known Artist", proj.CurrentTrack?.Artists[0].Name);
        Assert.Equal("Known Album", proj.CurrentTrack?.Album.Name);
        Assert.Equal("https://i.scdn.co/image/known", proj.CurrentTrack?.Image?.Url);
        Assert.Empty(outbound.Sent);
    }

    [Fact]
    public async Task PlayTrack_UriOnly_HydratesBeforePublishing_NotSyntheticUriTitle()
    {
        using var c = Make(out var host, out var proj, out _, ctx: Ctx("spotify:track:ignored"));

        await c.PlayTrackAsync("spotify:track:clicked");

        Assert.Contains("load:spotify:track:clicked", host.Calls);
        Assert.Equal("T:spotify:track:clicked", proj.CurrentTrack?.Title);
        Assert.NotEqual("spotify:track:clicked", proj.CurrentTrack?.Title);
    }

    [Fact]
    public async Task NoActiveDevice_Pause_RoutesLocal_NotForward()
    {
        using var c = Make(out var host, out _, out var outbound);
        await c.PauseAsync();
        Assert.Contains("pause", host.Calls);
        Assert.Empty(outbound.Sent);
    }

    [Fact]
    public async Task NoActiveDevice_Resume_GhostResumesFromClusterSnapshot()
    {
        using var c = Make(out var host, out var proj, out _, ctx: Ctx());
        proj.OnCluster(Cluster("", Remote("spotify:track:ghost"), pos: 5000));   // ghost: cluster has a track, nobody active
        await c.ResumeAsync();
        await Task.Delay(20);
        Assert.Contains("load:spotify:track:ghost", host.Calls);   // seeded from the cluster
        Assert.Contains("seek:5000", host.Calls);                  // resumed at the cluster position
        Assert.Contains("play", host.Calls);
    }

    [Fact]
    public async Task Ended_AutoAdvances_ToNextTrack()
    {
        using var c = Make(out var host, out _, out _);
        await c.PlayAsync("spotify:playlist:p");   // a
        host.Calls.Clear();
        host.Emit(new AudioHostSignal(AudioHostSignalKind.Ended, 60000));
        await Task.Delay(60);
        Assert.Contains("load:spotify:track:b", host.Calls);
    }

    [Fact]
    public async Task RemoteActive_Pause_Seek_Volume_Play_AllForward()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out var outbound, extra: new[] { events });
        proj.OnCluster(Cluster("other-device"));
        await c.PauseAsync();
        await c.SeekAsync(4242);
        await c.SetVolumeAsync(0.5);
        await c.PlayAsync("spotify:playlist:p");
        Assert.DoesNotContain(host.Calls, x => x is "pause" or "play");   // nothing driven locally
        Assert.Contains(outbound.Sent, s => s.Json.Contains("pause"));
        Assert.Contains(outbound.Sent, s => s.Json.Contains("seek_to") && s.Json.Contains("4242"));
        Assert.Equal((int)System.Math.Round(0.5 * 65535), outbound.LastVolume);   // volume via the dedicated connect/volume PUT
        Assert.DoesNotContain(outbound.Sent, s => s.Json.Contains("set_volume"));  // NOT a player/command verb
        Assert.Contains(outbound.Sent, s => s.Json.Contains("\"endpoint\":\"play\"") && s.Json.Contains("spotify:playlist:p"));
        Assert.All(outbound.Sent, s => Assert.Equal("other-device", s.Target));
    }

    [Fact]
    public async Task RemoteActive_Repeat_SplitsIntoTrackThenContext()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));
        await c.SetRepeatAsync(RepeatMode.Context);
        Assert.Equal(2, outbound.Sent.Count);
        Assert.Contains("set_repeating_track", outbound.Sent[0].Json);
        Assert.Contains("set_repeating_context", outbound.Sent[1].Json);
        Assert.Contains("true", outbound.Sent[1].Json);   // context = true
    }

    [Fact]
    public async Task RemoteActive_Enqueue_SendsAddToQueueTrackObject_NotFlatUri()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));
        await c.EnqueueAsync("spotify:track:x");
        using var doc = JsonDocument.Parse(outbound.LastJson!);
        var cmd = doc.RootElement.GetProperty("command");
        Assert.Equal("add_to_queue", cmd.GetProperty("endpoint").GetString());
        Assert.Equal("spotify:track:x", cmd.GetProperty("track").GetProperty("uri").GetString());
        Assert.False(cmd.TryGetProperty("uri", out _));   // NOT the legacy flat command.uri
        Assert.Equal("other-device", outbound.LastTarget);
    }

    [Fact]
    public async Task RemoteActive_PlayOrdered_EmbedsVisibleOrder_AndSkipTo()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));
        await c.PlayOrderedAsync("spotify:playlist:p", new[]
        {
            new PlaybackContextTrack("spotify:track:c", "uc"),
            new PlaybackContextTrack("spotify:track:a", "ua"),
            new PlaybackContextTrack("spotify:track:b", "ub"),
        }, startIndex: 1);

        using var doc = JsonDocument.Parse(outbound.LastJson!);
        var cmd = doc.RootElement.GetProperty("command");
        Assert.Equal("play", cmd.GetProperty("endpoint").GetString());
        var tracks = cmd.GetProperty("context").GetProperty("pages")[0].GetProperty("tracks");
        Assert.Equal("spotify:track:c", tracks[0].GetProperty("uri").GetString());   // visible order, verbatim
        Assert.Equal("spotify:track:a", tracks[1].GetProperty("uri").GetString());
        Assert.Equal("spotify:track:b", tracks[2].GetProperty("uri").GetString());
        var skip = cmd.GetProperty("prepare_play_options").GetProperty("skip_to");
        Assert.Equal("spotify:track:a", skip.GetProperty("track_uri").GetString());  // startIndex 1
        Assert.Equal("ua", skip.GetProperty("track_uid").GetString());
        Assert.Equal(1, skip.GetProperty("track_index").GetInt32());
    }

    [Fact]
    public async Task Local_PlayOrdered_HonorsEmbeddedOrder_NotResolver()
    {
        // Resolver's fixed list is [x]; the visible order is [b,a]. Local play must honor the SUPPLIED order.
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:x"));
        await c.PlayOrderedAsync("spotify:playlist:p", new[]
        {
            new PlaybackContextTrack("spotify:track:b", "ub"),
            new PlaybackContextTrack("spotify:track:a", "ua"),
        }, startIndex: 0);
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:b", host.Calls);        // embedded order honored
        Assert.DoesNotContain("load:spotify:track:x", host.Calls);  // NOT the resolver's list
    }

    [Fact]
    public async Task AnotherDeviceBecomesActive_StopsLocalPlayback()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out _, extra: new[] { events });
        await c.PlayAsync("spotify:playlist:p");
        Assert.True(host.IsPlaying);
        proj.OnCluster(Cluster("other-device"));   // someone else takes over
        Assert.Contains("stop", host.Calls);
        Assert.False(host.IsPlaying);
        Assert.Equal(1, events.Count(EvKind.BecameInactive));
    }

    [Fact]
    public async Task TransferToSelf_GhostResumes_TransferAway_ForwardsAndStops()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out var outbound, extra: new[] { events });
        proj.OnCluster(Cluster("", Remote("spotify:track:ghost"), pos: 1000));
        await c.TransferToAsync("us");                 // self → ghost resume
        await Task.Delay(20);
        Assert.Contains("load:spotify:track:ghost", host.Calls);

        host.Calls.Clear();
        await c.TransferToAsync("other-device");        // away → forward + stop
        Assert.Contains(outbound.Transfers, t => t.From == "us" && t.Target == "other-device");
        Assert.Contains("stop", host.Calls);
        Assert.Equal(1, events.Count(EvKind.BecameInactive));
    }

    [Fact]
    public async Task RemoteViewer_TransferToAnotherDevice_UsesConnectTransfer_WithoutInactive()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out var outbound, extra: new[] { events });
        proj.OnCluster(Cluster("active-device", Remote("spotify:track:remote"), playing: true));

        await c.TransferToAsync("target-device");

        Assert.Contains(outbound.Transfers, t => t.From == "active-device" && t.Target == "target-device");
        Assert.DoesNotContain("stop", host.Calls);
        Assert.Equal(0, events.Count(EvKind.BecameInactive));
    }

    [Fact]
    public async Task ActiveOwner_TransferFailure_DoesNotStopOrPublishInactive()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out _, out var outbound, extra: new[] { events });
        await c.PlayAsync("spotify:playlist:p");
        Assert.True(host.IsPlaying);
        outbound.TransferOk = false;
        host.Calls.Clear();

        await c.TransferToAsync("target-device");

        Assert.Contains(outbound.Transfers, t => t.From == "us" && t.Target == "target-device");
        Assert.DoesNotContain("stop", host.Calls);
        Assert.True(host.IsPlaying);
        Assert.Equal(0, events.Count(EvKind.BecameInactive));
    }

    [Fact]
    public async Task RemoteViewer_ActiveDeviceSwitch_DoesNotPublishInactive()
    {
        var events = new RecordingProjection();
        using var c = Make(out var host, out var proj, out _, extra: new[] { events });
        proj.OnCluster(Cluster("remote-a", Remote("spotify:track:a"), playing: true));
        proj.OnCluster(Cluster("remote-b", Remote("spotify:track:b"), playing: true));

        Assert.DoesNotContain("stop", host.Calls);
        Assert.Equal(0, events.Count(EvKind.BecameInactive));
    }

    [Fact]
    public async Task ActiveOwner_ActiveDeviceClears_PublishesInactiveOnce()
    {
        var events = new RecordingProjection();
        using var c = Make(out _, out var proj, out _, extra: new[] { events });
        await c.PlayAsync("spotify:playlist:p");
        proj.OnCluster(Cluster("us", Remote("spotify:track:a"), playing: true));

        proj.OnCluster(Cluster(""));

        Assert.Equal(1, events.Count(EvKind.BecameInactive));
    }

    [Fact]
    public async Task InboundCommand_AlwaysLocal_EvenWhenClusterShowsAnotherActive()
    {
        using var c = Make(out var host, out var proj, out _, ctx: Ctx("spotify:track:a"));
        await c.PlayAsync("spotify:playlist:p");
        proj.OnCluster(Cluster("other-device"));   // routing would say "forward"...
        host.Calls.Clear();
        ConnectCommand.TryParse(new WireRequest("k", "hm://connect-state/v1/player/command",
            Encoding.UTF8.GetBytes("{\"command\":{\"endpoint\":\"pause\"}}"), NoHeaders), out var cmd);
        c.HandleRemoteCommand(cmd);                 // ...but an inbound REQUEST is for us → drive local
        await Task.Delay(20);
        Assert.Contains("pause", host.Calls);
    }

    // ── Phase A: inbound play resolves the context (+ skip_to / embedded pages) ───────────────────────────────────────
    static void Dispatch(PlaybackController c, string commandJson)
    {
        ConnectCommand.TryParse(new WireRequest("k", "hm://connect-state/v1/player/command",
            Encoding.UTF8.GetBytes(commandJson), NoHeaders), out var cmd);
        c.HandleRemoteCommand(cmd);
    }

    [Fact]
    public async Task InboundPlay_Context_ResolvesAndPlaysFirstTrack()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\"}}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:a", host.Calls);
        Assert.Contains("play", host.Calls);
    }

    [Fact]
    public async Task InboundPlay_SkipToUid_StartsAtThatTrack()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b", "spotify:track:c"));
        Dispatch(c, "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\"},\"prepare_play_options\":{\"skip_to\":{\"track_uid\":\"uid2\"}}}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:c", host.Calls);   // uid2 → index 2
    }

    [Fact]
    public async Task InboundPlay_SkipToIndex_StartsAtThatTrack()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b", "spotify:track:c"));
        Dispatch(c, "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\"},\"prepare_play_options\":{\"skip_to\":{\"track_index\":1}}}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:b", host.Calls);
    }

    [Fact]
    public async Task InboundPlay_EmbeddedPages_PlayVerbatim_OverResolver()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:x"));   // the resolver's fixed list
        Dispatch(c, "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\",\"pages\":[{\"tracks\":[{\"uri\":\"spotify:track:e1\",\"uid\":\"u1\"},{\"uri\":\"spotify:track:e2\",\"uid\":\"u2\"}]}]}}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:e1", host.Calls);          // embedded pages win
        Assert.DoesNotContain("load:spotify:track:x", host.Calls);
    }

    // ── Phase B: the queue verbs (add_to_queue / set_queue / set_options) + prev<3s ──────────────────────────────────
    const string PlayP = "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\"}}}";

    [Fact]
    public async Task InboundAddToQueue_WhenIdle_StartsPlayingIt()
    {
        using var c = Make(out var host, out var proj, out _, ctx: new FakeContextResolver());   // empty context → idle
        Dispatch(c, "{\"command\":{\"endpoint\":\"add_to_queue\",\"track\":{\"uri\":\"spotify:track:q1\",\"uid\":\"uq1\"}}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:q1", host.Calls);
        Assert.Equal("spotify:track:q1", proj.CurrentTrack!.Uri);
    }

    [Fact]
    public async Task InboundAddToQueue_WhilePlaying_EnqueuesIntoUpNext()
    {
        using var c = Make(out _, out var proj, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, PlayP);
        await Task.Delay(30);
        Dispatch(c, "{\"command\":{\"endpoint\":\"add_to_queue\",\"track\":{\"uri\":\"spotify:track:q1\",\"uid\":\"uq1\"}}}");
        await Task.Delay(30);
        Assert.Contains(proj.Queue, e => e.Bucket == QueueBucket.UserQueue && e.Track.Uri == "spotify:track:q1");
    }

    [Fact]
    public async Task InboundSetQueue_ReplacesUpNext()
    {
        using var c = Make(out _, out var proj, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, PlayP);
        await Task.Delay(20);
        Dispatch(c, "{\"command\":{\"endpoint\":\"add_to_queue\",\"track\":{\"uri\":\"spotify:track:old\"}}}");
        await Task.Delay(20);
        Dispatch(c, "{\"command\":{\"endpoint\":\"set_queue\",\"next_tracks\":[" +
            "{\"uri\":\"spotify:track:n1\",\"uid\":\"u1\",\"provider\":\"queue\"}," +
            "{\"uri\":\"spotify:track:n2\",\"uid\":\"u2\",\"provider\":\"queue\"}]}}");
        await Task.Delay(30);
        var uq = proj.Queue.Where(e => e.Bucket == QueueBucket.UserQueue).Select(e => e.Track.Uri).ToArray();
        Assert.Equal(new[] { "spotify:track:n1", "spotify:track:n2" }, uq);   // 'old' replaced
        Assert.DoesNotContain(proj.Queue, e => e.Track.Uri == "spotify:track:old");
    }

    [Fact]
    public async Task InboundSetQueue_OnlyQueueProviderRows_BecomeUserQueue()
    {
        using var c = Make(out _, out var proj, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, PlayP);
        await Task.Delay(20);
        // next_tracks = user queue (provider:queue) THEN context continuation (provider:context) — queue rows land in
        // UserQueue; context continuation rows reconcile into Upcoming (§6 F8 full reconcile).
        Dispatch(c, "{\"command\":{\"endpoint\":\"set_queue\",\"next_tracks\":[" +
            "{\"uri\":\"spotify:track:n1\",\"uid\":\"q1\",\"provider\":\"queue\"}," +
            "{\"uri\":\"spotify:track:n2\",\"uid\":\"\",\"provider\":\"queue\"}," +
            "{\"uri\":\"spotify:track:cx\",\"uid\":\"h1\",\"provider\":\"context\"}," +
            "{\"uri\":\"spotify:track:cy\",\"uid\":\"h2\",\"provider\":\"context\"}]}}");
        await Task.Delay(30);
        var uq = proj.Queue.Where(e => e.Bucket == QueueBucket.UserQueue).Select(e => e.Track.Uri).ToArray();
        Assert.Equal(new[] { "spotify:track:n1", "spotify:track:n2" }, uq);
        var up = proj.Queue.Where(e => e.Bucket == QueueBucket.NextUp).Select(e => e.Track.Uri).ToArray();
        Assert.Equal(new[] { "spotify:track:cx", "spotify:track:cy" }, up);
    }

    [Fact]
    public async Task InboundSetQueue_DropsDelimiterRows()
    {
        using var c = Make(out _, out var proj, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, PlayP);
        await Task.Delay(20);
        Dispatch(c, "{\"command\":{\"endpoint\":\"set_queue\",\"next_tracks\":[" +
            "{\"uri\":\"spotify:track:n1\",\"provider\":\"queue\"}," +
            "{\"uri\":\"spotify:delimiter\",\"uid\":\"delimiter0\",\"provider\":\"context\"}]}}");
        await Task.Delay(30);
        var uq = proj.Queue.Where(e => e.Bucket == QueueBucket.UserQueue).Select(e => e.Track.Uri).ToArray();
        Assert.Equal(new[] { "spotify:track:n1" }, uq);
        Assert.DoesNotContain(proj.Queue, e => e.Track.Uri == "spotify:delimiter");
    }

    [Fact]
    public async Task Local_PlayNext_InsertsAtFrontOfUserQueue()
    {
        using var c = Make(out _, out var proj, out var outbound, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        await c.PlayAsync("spotify:playlist:p");            // local: seed a resident context
        await c.EnqueueAsync("spotify:track:existing");     // a pre-existing user-queue item
        await c.PlayNextAsync(new[]
        {
            new PlaybackContextTrack("spotify:track:t1", ""),
            new PlaybackContextTrack("spotify:track:t2", ""),
        });
        var uq = proj.Queue.Where(e => e.Bucket == QueueBucket.UserQueue).Select(e => e.Track.Uri).ToArray();
        Assert.Equal(new[] { "spotify:track:t1", "spotify:track:t2", "spotify:track:existing" }, uq);  // play-next at front
        Assert.Empty(outbound.Sent);                        // local → nothing forwarded
    }

    [Fact]
    public async Task RemoteActive_PlayNext_SendsSetQueue_InsertedRowsAreQueueProvider()
    {
        using var c = Make(out _, out var proj, out var outbound, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        proj.OnCluster(Cluster("other-device"));
        await c.PlayNextAsync(new[]
        {
            new PlaybackContextTrack("spotify:track:t1", "q1"),
            new PlaybackContextTrack("spotify:track:t2", ""),
        });
        using var doc = JsonDocument.Parse(outbound.LastJson!);
        var cmd = doc.RootElement.GetProperty("command");
        Assert.Equal("set_queue", cmd.GetProperty("endpoint").GetString());
        Assert.Empty(cmd.GetProperty("prev_tracks").EnumerateArray());
        var next = cmd.GetProperty("next_tracks");
        Assert.Equal("spotify:track:t1", next[0].GetProperty("uri").GetString());
        Assert.Equal("queue", next[0].GetProperty("provider").GetString());
        Assert.Equal("spotify:track:t2", next[1].GetProperty("uri").GetString());
        Assert.Equal("queue", next[1].GetProperty("provider").GetString());
        Assert.Equal("other-device", outbound.LastTarget);
    }

    [Fact]
    public async Task RemoteActive_PlayNext_EchoesQueueRevisionFromCluster()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device") with { QueueRevision = "10355548321371651421" });   // threaded from the proto
        await c.PlayNextAsync(new[] { new PlaybackContextTrack("spotify:track:t1", "") });
        using var doc = JsonDocument.Parse(outbound.LastJson!);
        Assert.Equal(10355548321371651421UL,
            doc.RootElement.GetProperty("command").GetProperty("queue_revision").GetUInt64());
    }

    [Fact]
    public async Task RemoteActive_PlayNext_RewritesClusterQueue_InsertedFrontThenDeviceQueueVerbatim()
    {
        using var c = Make(out _, out var proj, out var outbound);
        // The active remote device's REAL queue (from its cluster): a queued row, then a context-continuation row, plus history.
        proj.OnCluster(Cluster("other-device") with
        {
            QueueRevision = "42",
            NextTracks = new[]
            {
                new RemoteTrack("spotify:track:eq", "", "", "", "", "", null, 0, Uid: "uq", Provider: "queue"),
                new RemoteTrack("spotify:track:cx", "", "", "", "", "", null, 0, Uid: "uc", Provider: "context"),
            },
            PrevTracks = new[]
            {
                new RemoteTrack("spotify:track:hist", "", "", "", "", "", null, 0, Uid: "uh", Provider: "context"),
            },
        });
        await c.PlayNextAsync(new[] { new PlaybackContextTrack("spotify:track:t1", "") });

        using var doc = JsonDocument.Parse(outbound.LastJson!);
        var cmd = doc.RootElement.GetProperty("command");
        Assert.Equal("set_queue", cmd.GetProperty("endpoint").GetString());
        Assert.Equal(42UL, cmd.GetProperty("queue_revision").GetUInt64());   // from the same cluster snapshot, not 0

        var prevT = cmd.GetProperty("prev_tracks");                          // device history echoed verbatim (NOT empty)
        Assert.Equal("spotify:track:hist", prevT[0].GetProperty("uri").GetString());
        Assert.Equal("context", prevT[0].GetProperty("provider").GetString());

        var next = cmd.GetProperty("next_tracks");
        Assert.Equal("spotify:track:t1", next[0].GetProperty("uri").GetString());   // inserted at the FRONT
        Assert.Equal("queue", next[0].GetProperty("provider").GetString());
        Assert.Equal("spotify:track:eq", next[1].GetProperty("uri").GetString());   // then the device's own queue row...
        Assert.Equal("queue", next[1].GetProperty("provider").GetString());
        Assert.Equal("spotify:track:cx", next[2].GetProperty("uri").GetString());   // ...then its context continuation
        Assert.Equal("context", next[2].GetProperty("provider").GetString());
    }

    [Fact]
    public async Task InboundSetOptions_RepeatTrack_NextStaysOnSameTrack()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, PlayP);
        await Task.Delay(20);
        Dispatch(c, "{\"command\":{\"endpoint\":\"set_options\",\"repeating_track\":true}}");
        await Task.Delay(20);
        host.Calls.Clear();
        Dispatch(c, "{\"command\":{\"endpoint\":\"skip_next\"}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:a", host.Calls);   // repeat-one → reloads a, not b
    }

    [Fact]
    public async Task InboundSkipPrev_After3s_RestartsCurrentTrack()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, PlayP);
        await Task.Delay(20);
        host.PositionMs = 5000;
        host.Calls.Clear();
        Dispatch(c, "{\"command\":{\"endpoint\":\"skip_prev\"}}");
        await Task.Delay(30);
        Assert.Contains("seek:0", host.Calls);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("load:"));
    }

    [Fact]
    public async Task InboundSkipPrev_Within3s_StepsToPrevTrack()
    {
        using var c = Make(out var host, out _, out _, ctx: new FakeContextResolver("spotify:track:a", "spotify:track:b"));
        Dispatch(c, "{\"command\":{\"endpoint\":\"play\",\"context\":{\"uri\":\"spotify:playlist:p\"},\"prepare_play_options\":{\"skip_to\":{\"track_index\":1}}}}");
        await Task.Delay(20);   // current = b
        host.PositionMs = 1000;
        host.Calls.Clear();
        Dispatch(c, "{\"command\":{\"endpoint\":\"skip_prev\"}}");
        await Task.Delay(30);
        Assert.Contains("load:spotify:track:a", host.Calls);   // stepped back to a
    }

    // ── Volume parity: dedicated connect/volume endpoint + read the ACTIVE device's volume + react to remote changes ───
    [Fact]
    public async Task RemoteActive_SetVolume_UsesConnectVolumeEndpoint_NotPlayerCommand()
    {
        using var c = Make(out _, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));
        await c.SetVolumeAsync(0.25);
        Assert.Equal((int)System.Math.Round(0.25 * 65535), outbound.LastVolume);
        Assert.Equal("other-device", outbound.Volumes[^1].Target);
        Assert.Empty(outbound.Sent);   // no player/command verb at all
    }

    [Fact]
    public void Cluster_ActiveDeviceVolume_DrivesSlider_AndRemoteChangeReacts()
    {
        var proj = new NowPlayingProjection("us", () => 1_000_000);   // clock far ahead → outside any local-command window
        proj.OnCluster(Cluster("other-device") with { ActiveVolume0_65535 = 32768 });
        Assert.Equal(0.5, proj.Volume, 2);   // the active device's volume drives the slider
        proj.OnCluster(Cluster("other-device") with { ActiveVolume0_65535 = 13107 });   // a remote controller turned it down
        Assert.Equal(0.2, proj.Volume, 2);   // reacted to the remote change
    }

    // ── Local playback rejection: with OnLocalPlaybackRejected set (local audio unsupported), every local play path aborts
    // + fires the hook (the app's "choose a remote device" toast); remote forwarding is untouched. Default (null) = the
    // existing tests above, which prove local playback still works when the hook is absent. ─────────────────────────────
    [Fact]
    public async Task LocalPlay_Rejected_WhenHookSet()
    {
        using var c = Make(out var host, out _, out var outbound);   // no active device → routes local
        int rejects = 0; c.OnLocalPlaybackRejected = () => rejects++;
        await c.PlayAsync("spotify:playlist:p");
        await Task.Delay(20);
        Assert.DoesNotContain(host.Calls, x => x == "play" || x.StartsWith("load:"));   // nothing loaded / played locally
        Assert.True(rejects >= 1);                                                       // the toast hook fired
        Assert.Empty(outbound.Sent);                                                     // and nothing was forwarded
    }

    [Fact]
    public async Task Resume_GhostResume_Rejected_WhenHookSet()
    {
        using var c = Make(out var host, out var proj, out _, ctx: Ctx());
        proj.OnCluster(Cluster("", Remote("spotify:track:ghost"), pos: 5000));   // a cluster track, nobody active → local ghost-resume
        int rejects = 0; c.OnLocalPlaybackRejected = () => rejects++;
        await c.ResumeAsync();
        await Task.Delay(20);
        Assert.DoesNotContain(host.Calls, x => x == "play" || x.StartsWith("load:"));
        Assert.True(rejects >= 1);
    }

    [Fact]
    public async Task TransferToSelf_Rejected_WhenHookSet()
    {
        using var c = Make(out var host, out var proj, out _);
        proj.OnCluster(Cluster("", Remote("spotify:track:ghost"), pos: 1000));
        int rejects = 0; c.OnLocalPlaybackRejected = () => rejects++;
        await c.TransferToAsync("us");   // transfer to THIS device = local playback → rejected
        await Task.Delay(20);
        Assert.DoesNotContain(host.Calls, x => x == "play" || x.StartsWith("load:"));
        Assert.True(rejects >= 1);
    }

    [Fact]
    public async Task RemoteForward_Unaffected_WhenHookSet()
    {
        using var c = Make(out var host, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));           // another device active → routes REMOTE
        int rejects = 0; c.OnLocalPlaybackRejected = () => rejects++;
        await c.PlayAsync("spotify:playlist:p");
        await c.PauseAsync();
        Assert.Equal(0, rejects);                                                        // remote routing never trips the local hook
        Assert.Contains(outbound.Sent, s => s.Json.Contains("\"endpoint\":\"play\""));   // forwarded to the remote device
        Assert.DoesNotContain(host.Calls, x => x == "play");                             // nothing played locally
    }
}
