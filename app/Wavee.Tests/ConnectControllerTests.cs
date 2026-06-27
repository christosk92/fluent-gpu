using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

    static Track Trk(string uri) => new(uri[(uri.LastIndexOf(':') + 1)..], uri, "T:" + uri,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 60000, false, null);

    static Func<string, CancellationToken, Task<IReadOnlyList<Track>>> Ctx(params string[] uris)
        => (_, _) => Task.FromResult<IReadOnlyList<Track>>(uris.Select(Trk).ToArray());

    sealed class RecordingAudioHost : IAudioHost
    {
        public readonly List<string> Calls = new();
        readonly SimpleSubject<AudioHostSignal> _sig = new();
        public IObservable<AudioHostSignal> Signals => _sig;
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsBuffering => false;
        public void Load(in AudioStreamHandle s) { Calls.Add("load:" + s.TrackUri); }
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
        public string? LastTarget => Sent.Count > 0 ? Sent[^1].Target : null;
        public string? LastJson => Sent.Count > 0 ? Sent[^1].Json : null;
        public Task SendAsync(string targetDeviceId, string commandJson, CancellationToken ct = default)
        { Sent.Add((targetDeviceId, commandJson)); return Task.CompletedTask; }
    }

    static ClusterDelta Cluster(string active, RemoteTrack? track = null, long pos = 0, bool playing = false) =>
        new(active, track is not null, track ?? default, "spotify:playlist:ctx",
            playing, !playing, false, pos, 0, 0, track?.DurationMs ?? 0, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

    static RemoteTrack Remote(string uri, long dur = 200000) => new(uri, "G", "A", "spotify:artist:a", "Al", "spotify:album:al", null, dur);

    PlaybackController Make(out RecordingAudioHost host, out NowPlayingProjection proj, out RecordingOutbound outbound,
        Func<string, CancellationToken, Task<IReadOnlyList<Track>>>? ctx = null, Func<long>? clock = null)
    {
        host = new RecordingAudioHost();
        proj = new NowPlayingProjection("us", clock ?? (() => 0));
        outbound = new RecordingOutbound();
        return new PlaybackController(host, new StubTrackResolver(), proj, ctx ?? Ctx("spotify:track:a", "spotify:track:b"), "us", outbound);
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
        using var c = Make(out var host, out var proj, out var outbound);
        proj.OnCluster(Cluster("other-device"));
        await c.PauseAsync();
        await c.SeekAsync(4242);
        await c.SetVolumeAsync(0.5);
        await c.PlayAsync("spotify:playlist:p");
        Assert.Empty(host.Calls.Where(x => x is "pause" or "play"));   // nothing driven locally
        Assert.Contains(outbound.Sent, s => s.Json.Contains("pause"));
        Assert.Contains(outbound.Sent, s => s.Json.Contains("seek_to") && s.Json.Contains("4242"));
        Assert.Contains(outbound.Sent, s => s.Json.Contains("set_volume"));
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
    public async Task AnotherDeviceBecomesActive_StopsLocalPlayback()
    {
        using var c = Make(out var host, out var proj, out _);
        await c.PlayAsync("spotify:playlist:p");
        Assert.True(host.IsPlaying);
        proj.OnCluster(Cluster("other-device"));   // someone else takes over
        Assert.Contains("stop", host.Calls);
        Assert.False(host.IsPlaying);
    }

    [Fact]
    public async Task TransferToSelf_GhostResumes_TransferAway_ForwardsAndStops()
    {
        using var c = Make(out var host, out var proj, out var outbound);
        proj.OnCluster(Cluster("", Remote("spotify:track:ghost"), pos: 1000));
        await c.TransferToAsync("us");                 // self → ghost resume
        await Task.Delay(20);
        Assert.Contains("load:spotify:track:ghost", host.Calls);

        host.Calls.Clear();
        await c.TransferToAsync("other-device");        // away → forward + stop
        Assert.Contains(outbound.Sent, s => s.Json.Contains("transfer") && s.Target == "other-device");
        Assert.Contains("stop", host.Calls);
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
}
