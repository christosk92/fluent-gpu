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

// Stage E — the controller: local arm (resolve -> host) when we're active, outbound-forward when another device is active,
// inbound remote-command translation, and Ended -> auto-advance.
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
        public string? LastTarget; public string? LastJson;
        public Task SendAsync(string targetDeviceId, string commandJson, CancellationToken ct = default)
        { LastTarget = targetDeviceId; LastJson = commandJson; return Task.CompletedTask; }
    }

    static ClusterDelta ActiveOther(string id) =>
        new(id, false, default, null, false, true, false, 0, 0, 0, 0, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

    [Fact]
    public async Task LocalPlay_Resolves_Loads_Plays_AndProjectionReflects()
    {
        var host = new RecordingAudioHost();
        var proj = new NowPlayingProjection("us", () => 0);
        using var c = new PlaybackController(host, new StubTrackResolver(), proj, Ctx("spotify:track:a", "spotify:track:b"), "us");
        await c.PlayAsync("spotify:playlist:p");
        Assert.Contains("load:spotify:track:a", host.Calls);
        Assert.Contains("play", host.Calls);
        Assert.True(proj.IsPlaying);
        Assert.Equal("spotify:track:a", proj.CurrentTrack!.Uri);
    }

    [Fact]
    public async Task Ended_AutoAdvances_ToNextTrack()
    {
        var host = new RecordingAudioHost();
        var proj = new NowPlayingProjection("us", () => 0);
        using var c = new PlaybackController(host, new StubTrackResolver(), proj, Ctx("spotify:track:a", "spotify:track:b"), "us");
        await c.PlayAsync("spotify:playlist:p");
        host.Calls.Clear();
        host.Emit(new AudioHostSignal(AudioHostSignalKind.Ended, 60000));
        await Task.Delay(60);
        Assert.Contains("load:spotify:track:b", host.Calls);
    }

    [Fact]
    public async Task NotActive_ForwardsAsOutboundCommand()
    {
        var host = new RecordingAudioHost();
        var proj = new NowPlayingProjection("us", () => 0);
        var outbound = new RecordingOutbound();
        using var c = new PlaybackController(host, new StubTrackResolver(), proj, Ctx(), "us", outbound);
        proj.OnCluster(ActiveOther("other-device"));   // another device is active
        await c.PauseAsync();
        Assert.DoesNotContain("pause", host.Calls);    // not driven locally
        Assert.Equal("other-device", outbound.LastTarget);
        Assert.Contains("pause", outbound.LastJson);

        await c.SeekAsync(4242);
        Assert.Contains("seek_to", outbound.LastJson);
        Assert.Contains("4242", outbound.LastJson);
    }

    [Fact]
    public async Task HandleRemoteCommand_Pause_DrivesLocalHost()
    {
        var host = new RecordingAudioHost();
        var proj = new NowPlayingProjection("us", () => 0);
        using var c = new PlaybackController(host, new StubTrackResolver(), proj, Ctx("spotify:track:a"), "us");
        await c.PlayAsync("spotify:playlist:p");   // we become locally active
        host.Calls.Clear();
        ConnectCommand.TryParse(new WireRequest("k", "hm://connect-state/v1/player/command",
            Encoding.UTF8.GetBytes("{\"command\":{\"endpoint\":\"pause\"}}"), NoHeaders), out var cmd);
        c.HandleRemoteCommand(cmd);
        await Task.Delay(20);
        Assert.Contains("pause", host.Calls);
    }

    [Fact]
    public async Task TransferOut_SendsTransfer_AndDropsLocalActive()
    {
        var host = new RecordingAudioHost();
        var proj = new NowPlayingProjection("us", () => 0);
        var outbound = new RecordingOutbound();
        using var c = new PlaybackController(host, new StubTrackResolver(), proj, Ctx("spotify:track:a"), "us", outbound);
        await c.PlayAsync("spotify:playlist:p");
        await c.TransferToAsync("other-device");
        Assert.Equal("other-device", outbound.LastTarget);
        Assert.Contains("transfer", outbound.LastJson);
    }
}
