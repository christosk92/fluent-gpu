using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using FluentGpu.Media.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// Clear adaptive sources load their manifest OFF the open's critical path (L2-10): <see cref="MfMediaPlayer.OpenAsync"/>
/// posts the source switch at once and attaches the parsed catalog when it arrives (<see cref="MfMediaSession.AttachManifest"/>,
/// adopted by the next pump on the UI thread), and a live manifest is re-fetched so its edge keeps moving. Driven through a
/// <see cref="FakeVideoEngine"/> and a scripted HTTP handler, so no MF engine and no network.
/// </summary>
public sealed class MfMediaLateManifestTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly RectF Rect = new(0, 0, 320, 180);

    private const string Mpd = "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT2S\"><Period><AdaptationSet contentType=\"video\"><Representation id=\"v\" bandwidth=\"1000\"><SegmentTemplate media=\"$Number$.m4s\" duration=\"2\" timescale=\"1\" startNumber=\"1\" /></Representation></AdaptationSet></Period></MPD>";

    private static void Pump(MfMediaSession s) => s.PumpVideo(default, Rect, 1f);

    private static async Task PumpUntil(MfMediaSession s, Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow + Limit;
        while (true)
        {
            Pump(s);
            if (done()) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The pumped state did not arrive within five seconds.");
            await Task.Delay(10);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not become true within five seconds.");
            await Task.Delay(10);
        }
    }

    // A manifest with one video group and one audio group; `end` is where its last segment ends (a live edge).
    private static AdaptiveManifest Manifest(bool live, double endSeconds)
    {
        var video = new QualityVariant("v720", 2_000_000, new SizeI(1280, 720), 30,
            new MediaContentType(Container.Dash, CodecId.H264, CodecId.None));
        var audio = new QualityVariant("a-en", 128_000, SizeI.Zero, 0,
            new MediaContentType(Container.Dash, CodecId.None, CodecId.Aac));
        AdaptiveSegment seg = new(new Uri("https://fixture.test/1.m4s"), 1, TimeSpan.FromSeconds(endSeconds - 2), TimeSpan.FromSeconds(2));
        return new AdaptiveManifest(new Uri("https://fixture.test/live.mpd"), AdaptiveManifestKind.Dash,
            live, false, live ? null : TimeSpan.FromSeconds(endSeconds), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(2), null,
            [
                new AdaptiveTrackGroup("video", AdaptiveTrackType.Video, null, TrackRole.Main,
                    [new AdaptiveRepresentation(video, null, [seg])], true),
                new AdaptiveTrackGroup("audio-en", AdaptiveTrackType.Audio, "en", TrackRole.Main,
                    [new AdaptiveRepresentation(audio, null, [seg])], true),
            ]);
    }

    private static (MfMediaSession session, MediaPlayerCore core, FakeVideoEngine engine) NewManifestlessSession()
    {
        var core = new MediaPlayerCore();
        var engine = new FakeVideoEngine();
        var session = new MfMediaSession(engine, 0, new MediaOpenOptions { StartPaused = true });
        session.ConnectSignals(new MediaSignalSink(core));
        return (session, core, engine);
    }

    // ── the session: a late attach republishes ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LateAttach_RepublishesCatalogTracksAndCommands_OnTheNextPump()
    {
        var (session, core, engine) = NewManifestlessSession();
        engine.MetadataLoaded = true; engine.DurationSeconds = 10; Pump(session);   // Ready, before any catalog exists
        Assert.Empty(core.Tracks.Video);
        Assert.False(core.Commands.Can(MediaCommandFlags.SelectVideoQuality));
        int pumps = 0;
        session.PumpRequested += () => pumps++;

        session.AttachManifest(Manifest(live: false, endSeconds: 10));
        Assert.True(pumps > 0);            // an attach from any thread asks for a pump, it never writes the sink itself
        Assert.Empty(core.Tracks.Video);   // ...so nothing is published until the UI-thread pump adopts it

        Pump(session);
        Assert.Single(core.Tracks.Video);
        Assert.Single(core.Tracks.Audio);
        Assert.Single(core.Qualities.Variants);
        Assert.Equal(new SizeI(1280, 720), core.VideoGeometry.Peek().DisplaySize);
        Assert.True(core.Commands.Can(MediaCommandFlags.SelectVideoQuality));
        Assert.True(core.Commands.Can(MediaCommandFlags.SelectAudioTrack));
        Assert.True(core.Commands.Can(MediaCommandFlags.Seek));   // the core transport set survives the republish
    }

    [Fact]
    public void AttachBeforeConnect_IsAdoptedByConnectSignals()
    {
        var core = new MediaPlayerCore();
        var session = new MfMediaSession(new FakeVideoEngine(), 0, new MediaOpenOptions());
        session.AttachManifest(Manifest(live: false, endSeconds: 10));

        session.ConnectSignals(new MediaSignalSink(core));

        Assert.Single(core.Tracks.Video);
        Assert.Single(core.Tracks.Audio);
    }

    [Fact]
    public void LateAttach_KeepsASidecarTrackAddedSinceConnect_WithItsSelection()
    {
        var (session, core, _) = NewManifestlessSession();
        MediaTrack sidecar = core.Tracks.AddExternalSubtitle(SubtitleSource.FromUri("https://fixture.test/en.vtt"), "en", "English");
        core.Tracks.Select(sidecar);
        core.Tracks.SetSyncOffset(sidecar, TimeSpan.FromMilliseconds(250));

        session.AttachManifest(Manifest(live: false, endSeconds: 10));
        Pump(session);

        Assert.Single(core.Tracks.Video);   // the manifest's tracks are registered...
        Assert.Single(core.Tracks.Audio);
        Assert.Same(sidecar, Assert.Single(core.Tracks.Text));   // ...beside the sidecar, which the catalog did not wipe
        Assert.Same(sidecar, core.Tracks.SelectedText.Peek());
        Assert.True(sidecar.IsSelected.Peek());
        Assert.Equal(TimeSpan.FromMilliseconds(250), core.Tracks.SyncOffsetOf(sidecar));
    }

    [Fact]
    public void LateAttach_ForcedManifestTextTrack_DoesNotTakeTheSelectionFromASelectedSidecar()
    {
        var (session, core, _) = NewManifestlessSession();
        MediaTrack sidecar = core.Tracks.AddExternalSubtitle(SubtitleSource.FromUri("https://fixture.test/en.vtt"), "en", "English");
        core.Tracks.Select(sidecar);
        AdaptiveManifest plain = Manifest(live: false, endSeconds: 10);
        var forced = new AdaptiveTrackGroup("text-en", AdaptiveTrackType.Text, "en", TrackRole.Subtitles,
            plain.TrackGroups[1].Representations, false, true);
        AdaptiveManifest withForced = plain with { TrackGroups = [.. plain.TrackGroups, forced] };

        session.AttachManifest(withForced);
        Pump(session);

        Assert.Equal(2, core.Tracks.Text.Count);   // the sidecar and the manifest's forced track
        Assert.Same(sidecar, core.Tracks.SelectedText.Peek());   // the rendered captions and the selected track agree
        Assert.True(sidecar.IsSelected.Peek());
        Assert.False(core.Tracks.Text[1].IsSelected.Peek());
    }

    [Fact]
    public void ForcedManifestTextTrack_IsSelected_WhenNothingElseIs()
    {
        var core = new MediaPlayerCore();
        var session = new MfMediaSession(new FakeVideoEngine(), 0, new MediaOpenOptions());
        AdaptiveManifest plain = Manifest(live: false, endSeconds: 10);
        var forced = new AdaptiveTrackGroup("text-en", AdaptiveTrackType.Text, "en", TrackRole.Subtitles,
            plain.TrackGroups[1].Representations, false, true);
        session.AttachManifest(plain with { TrackGroups = [.. plain.TrackGroups, forced] });

        session.ConnectSignals(new MediaSignalSink(core));

        MediaTrack text = Assert.Single(core.Tracks.Text);
        Assert.Same(text, core.Tracks.SelectedText.Peek());
    }

    [Fact]
    public void ConnectSignals_ClearsThePreviousSourcesTracks_EvenWithoutAManifest()
    {
        var core = new MediaPlayerCore();
        core.Tracks.Register(1, TrackKind.Video, null, "old video", TrackRole.Main, default, selected: true);
        core.Tracks.Register(2, TrackKind.Audio, "en", "old audio", TrackRole.Main, default, selected: true);
        core.Tracks.AddExternalSubtitle(SubtitleSource.FromUri("https://fixture.test/old.vtt"), "en", "Old");
        var session = new MfMediaSession(new FakeVideoEngine(), 0, new MediaOpenOptions());

        session.ConnectSignals(new MediaSignalSink(core));   // a manifestless (pending or failed) session

        Assert.Empty(core.Tracks.Video);
        Assert.Empty(core.Tracks.Audio);
        Assert.Empty(core.Tracks.Text);
        Assert.Null(core.Tracks.SelectedVideo.Peek());
        Assert.Null(core.Tracks.SelectedAudio.Peek());
    }

    [Fact]
    public void LiveRefresh_MovesTheEdge_WithoutResettingTheTracks()
    {
        var (session, core, _) = NewManifestlessSession();
        session.AttachManifest(Manifest(live: true, endSeconds: 12));
        Pump(session);
        MediaTrack trackBefore = Assert.Single(core.Tracks.Video);
        Assert.True(core.Timeline.Peek().IsLive);
        Assert.Equal(TimeSpan.FromSeconds(12), core.Timeline.Peek().LiveEdge);

        session.AttachManifest(Manifest(live: true, endSeconds: 20));   // the refreshed window
        Pump(session);

        Assert.Equal(TimeSpan.FromSeconds(20), core.Timeline.Peek().LiveEdge);
        Assert.Same(trackBefore, Assert.Single(core.Tracks.Video));   // a refresh never re-announces the catalog (the user's selection survives)
    }

    [Fact]
    public async Task Dispose_CancelsTheLifetime_AndALateAttachIsIgnored()
    {
        var (session, core, _) = NewManifestlessSession();
        CancellationToken lifetime = session.Lifetime;
        Assert.False(lifetime.IsCancellationRequested);

        await session.DisposeAsync().AsTask().WaitAsync(Limit);
        Assert.True(lifetime.IsCancellationRequested);

        session.AttachManifest(Manifest(live: false, endSeconds: 10));   // must not throw or publish
        Assert.Empty(core.Tracks.Video);
    }

    [Theory]
    [InlineData(0, 6.0)]       // the manifest names no period: the default
    [InlineData(100, 1.0)]     // floored
    [InlineData(5000, 5.0)]    // its own period
    [InlineData(300_000, 30.0)]   // capped
    public void LiveRefreshInterval_IsTheManifestsUpdatePeriod_Clamped(int periodMs, double expectedSeconds)
    {
        AdaptiveManifest m = Manifest(live: true, endSeconds: 12) with { MinimumUpdatePeriod = TimeSpan.FromMilliseconds(periodMs) };
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), MfMediaPlayer.LiveRefreshInterval(m));
    }

    // ── the backend: the open does not wait for the manifest ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AdaptiveOpen_PostsTheSourceAtOnce_AndAttachesTheManifestWhenItArrives()
    {
        var engine = new FakeVideoEngine();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (request, _) =>
        {
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Mpd), RequestMessage = request };
        });
        using var client = new HttpClient(handler);
        var backend = new MfMediaPlayer(() => engine, null, client);

        // The open returns while the manifest request is still parked (it used to await the fetch before SetSource).
        var session = (MfMediaSession)await backend
            .OpenAsync(MediaSource.FromAdaptive("https://fixture.test/stream.mpd"), new MediaOpenOptions { StartPaused = true }, CancellationToken.None)
            .AsTask().WaitAsync(Limit);
        Assert.Equal(1, engine.PostSetSourceCalls);
        Assert.Equal("https://fixture.test/stream.mpd", engine.LastSetSourceUrl);
        var core = new MediaPlayerCore();
        session.ConnectSignals(new MediaSignalSink(core));
        Pump(session);
        Assert.Empty(core.Tracks.Video);

        gate.SetResult();
        await PumpUntil(session, () => core.Tracks.Video.Count == 1);

        await session.DisposeAsync().AsTask().WaitAsync(Limit);
    }

    [Fact]
    public async Task AdaptiveOpen_AManifestThatCannotLoad_CostsTheCatalogNotThePlayback()
    {
        var engine = new FakeVideoEngine();
        var handler = new ScriptedHandler((request, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request }));
        using var client = new HttpClient(handler);
        var backend = new MfMediaPlayer(() => engine, null, client);

        var session = (MfMediaSession)await backend
            .OpenAsync(MediaSource.FromAdaptive("https://fixture.test/stream.mpd"),
                new MediaOpenOptions { StartPaused = true, Network = new NetworkOptions(MaxRetries: 0) }, CancellationToken.None)
            .AsTask().WaitAsync(Limit);
        await WaitFor(() => handler.Calls >= 1);

        var core = new MediaPlayerCore();
        session.ConnectSignals(new MediaSignalSink(core));
        Pump(session);
        Assert.Empty(core.Tracks.Video);   // no catalog...
        Assert.Equal(1, engine.PostSetSourceCalls);   // ...but the source was handed to MF regardless

        await session.DisposeAsync().AsTask().WaitAsync(Limit);
    }

    [Fact]
    public async Task LiveManifest_IsRefreshed_SoTheEdgeKeepsMoving_UntilTheSessionIsDisposed()
    {
        var engine = new FakeVideoEngine();
        // Call n answers a live HLS media playlist holding n one-second segments: the window grows by a segment per fetch.
        var handler = new ScriptedHandler((request, call) =>
        {
            var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n");
            for (int i = 0; i < call + 1; i++) sb.Append("#EXTINF:1.0,\ns").Append(i).Append(".ts\n");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString()), RequestMessage = request });
        });
        using var client = new HttpClient(handler);
        var backend = new MfMediaPlayer(() => engine, null, client);
        var session = (MfMediaSession)await backend
            .OpenAsync(MediaSource.FromAdaptive("https://fixture.test/live.m3u8"), new MediaOpenOptions { StartPaused = true }, CancellationToken.None)
            .AsTask().WaitAsync(Limit);
        var core = new MediaPlayerCore();
        session.ConnectSignals(new MediaSignalSink(core));

        await PumpUntil(session, () => core.Timeline.Peek().IsLive);
        MediaTrack trackBefore = Assert.Single(core.Tracks.Video);
        TimeSpan firstEdge = core.Timeline.Peek().LiveEdge;

        // The playlist names a 1 s target duration, so the refresh period is the 1 s floor: the next fetch grows the window.
        await PumpUntil(session, () => core.Timeline.Peek().LiveEdge > firstEdge);
        Assert.True(handler.Calls >= 2);
        Assert.Same(trackBefore, Assert.Single(core.Tracks.Video));

        await session.DisposeAsync().AsTask().WaitAsync(Limit);
        Assert.True(session.Lifetime.IsCancellationRequested);   // the refresh loop ends with the session
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, Interlocked.Increment(ref _calls));
    }
}
