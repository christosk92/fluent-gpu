using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class ProfessionalMediaTests
{
    [Fact]
    public void Core_ProfessionalSignalsHaveStableNonNullDefaults()
    {
        var core = new MediaPlayerCore();
        Assert.Equal(BufferingReason.None, core.Buffering.Peek().Reason);
        Assert.False(core.Timeline.Peek().IsLive);
        Assert.False(core.VideoGeometry.Peek().HasVideo);
        Assert.Equal(HdrFormat.Sdr, core.VideoColor.Peek().Hdr);
        Assert.Empty(core.Qualities.Variants);
        Assert.Equal(PlaybackStatistics.Empty, core.Statistics.Peek());
    }

    [Fact]
    public void State_DerivesInitialAndRebufferingReasonsThenClears()
    {
        var core = new MediaPlayerCore();
        core.SetState(PlaybackState.Opening);
        Assert.Equal(BufferingReason.Initial, core.Buffering.Peek().Reason);
        core.SetState(PlaybackState.Ready);
        Assert.Equal(BufferingReason.None, core.Buffering.Peek().Reason);
        core.SetState(PlaybackState.Stalled);
        Assert.Equal(BufferingReason.Rebuffering, core.Buffering.Peek().Reason);
        core.SetState(PlaybackState.Playing);
        Assert.Equal(BufferingReason.None, core.Buffering.Peek().Reason);
    }

    [Fact]
    public void Geometry_PublishesDisplaySizeAsNaturalSize()
    {
        var core = new MediaPlayerCore();
        core.SetVideoGeometry(new VideoGeometry(new SizeI(720, 576), new PixelRect(8, 0, 704, 576),
            new PixelAspectRatio(16, 15), 0, new SizeI(751, 576)));
        Assert.Equal(new SizeI(751, 576), core.NaturalSize.Peek());
        Assert.Equal(16d / 15d, core.VideoGeometry.Peek().SampleAspectRatio.Value, 6);
    }

    [Fact]
    public void QualitySet_SeparatesIntentFromActiveRepresentation()
    {
        var q = new QualitySet();
        var v = new QualityVariant("1080p", 5_000_000, new SizeI(1920, 1080), 60,
            new MediaContentType(Container.Mp4, CodecId.H264, CodecId.Aac));
        q.Variants.Add(v);
        q.PublishSelection(QualitySelection.Pin(v.Id));
        q.PublishActive(v);
        Assert.False(q.Selected.Peek().IsAuto);
        Assert.Same(v, q.Active.Peek());
    }

    [Fact]
    public void AdaptiveSource_CarriesManifestAndLowLatencyPolicy()
    {
        var source = Assert.IsType<AdaptiveSource>(MediaSource.FromAdaptive("https://example.test/live.mpd",
            new AdaptiveSourceOptions { ManifestKind = AdaptiveManifestKind.Dash, LatencyMode = LiveLatencyMode.LowLatency }));
        Assert.Equal(MediaKind.MfVideoOrFile, source.Kind);
        Assert.Equal(LiveLatencyMode.LowLatency, source.Options.LatencyMode);
    }

    [Fact]
    public async Task Builder_ForwardsNetworkAbrBufferAndLatencyToBackend()
    {
        var backend = new CaptureBackend();
        var net = new NetworkOptions(MaxRetries: 7);
        var buffer = BufferPolicy.LowLatencyLive;
        var player = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend)
            .WithNetwork(net).WithBuffering(buffer).WithAbr(AbrPolicy.Auto).Build();
        await player.OpenAsync(MediaSource.FromAdaptive("https://example.test/live.mpd",
            new AdaptiveSourceOptions { LatencyMode = LiveLatencyMode.LowLatency }), TestContext.Current.CancellationToken);
        Assert.Same(net, backend.Options!.Network);
        Assert.Same(buffer, backend.Options.Buffering);
        Assert.Same(AbrPolicy.Auto, backend.Options.Abr);
        Assert.Equal(LiveLatencyMode.LowLatency, backend.Options.LiveLatency);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Builder_CarriesTheSourceLivenessDeclarationToTheBackend()
    {
        // The host that RESOLVED the locator knows whether it is live; the backend must be told rather than left to
        // infer (Media Foundation reports a sliding DVR window as a finite duration, and its live probe is a bounded
        // read whose "false" can mean "not answered yet"). WithLiveness is the declaration; MediaOpenOptions.Liveness
        // is how it reaches the backend.
        var backend = new CaptureBackend();
        var player = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend).Build();

        await player.OpenAsync(MediaSource.FromUri("https://example.test/live.m3u8").WithKind(MediaKind.MfVideoOrFile)
            .WithLiveness(SourceLiveness.Live), TestContext.Current.CancellationToken);
        Assert.Equal(SourceLiveness.Live, backend.Options!.Liveness);

        await player.OpenAsync(MediaSource.FromUri("https://example.test/vod.mp4").WithKind(MediaKind.MfVideoOrFile)
            .WithLiveness(SourceLiveness.Vod), TestContext.Current.CancellationToken);
        Assert.Equal(SourceLiveness.Vod, backend.Options!.Liveness);

        // Undeclared is Auto — every existing caller keeps the backend's own inference, unchanged.
        await player.OpenAsync(MediaSource.FromUri("https://example.test/plain.mp4").WithKind(MediaKind.MfVideoOrFile),
            TestContext.Current.CancellationToken);
        Assert.Equal(SourceLiveness.Auto, backend.Options!.Liveness);

        await player.DisposeAsync();
    }

    [Fact]
    public void WithLiveness_IsAnImmutableBuilder_AndSurvivesTheSourceAlgebra()
    {
        MediaSource baseline = MediaSource.FromUri("https://example.test/live.m3u8");
        Assert.Equal(SourceLiveness.Auto, baseline.Liveness);

        MediaSource live = baseline.WithLiveness(SourceLiveness.Live);
        Assert.Equal(SourceLiveness.Auto, baseline.Liveness);      // the original record is untouched
        Assert.Equal(SourceLiveness.Live, live.Liveness);
        Assert.Equal(SourceLiveness.Live, live.WithKind(MediaKind.MfVideoOrFile).Liveness);
    }

    [Fact]
    public void TimelineInfo_HasDvrWindow_IsTheOneThresholdForOfferingARail()
    {
        // Under the threshold a "seek" is a jitter the sliding window invalidates before the pointer is released; the
        // backend drops Seek there and the transport must not render a rail either. One constant, both decisions.
        Assert.Equal(TimeSpan.FromSeconds(30), TimelineInfo.MinDvrWindow);
        Assert.False(LiveTimeline(0, 6).HasDvrWindow);            // true-live, 3 segments
        Assert.False(LiveTimeline(0, 29.999).HasDvrWindow);
        Assert.True(LiveTimeline(0, 30).HasDvrWindow);
        Assert.True(LiveTimeline(60, 240).HasDvrWindow);
        // A VOD timeline with a wide seekable range is NOT a DVR window (it has a duration to map the rail onto).
        Assert.False(new TimelineInfo(false, TimeSpan.Zero, TimeSpan.FromSeconds(600), TimeSpan.Zero,
            TimeSpan.Zero, false, Array.Empty<MediaChapter>()).HasDvrWindow);

        static TimelineInfo LiveTimeline(double start, double end) => new(true,
            TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), TimeSpan.FromSeconds(end),
            TimeSpan.Zero, true, Array.Empty<MediaChapter>());
    }

    private sealed class CaptureBackend : IMediaBackend
    {
        public MediaOpenOptions? Options;
        public MediaCapabilities Capabilities => new(true, false, false);
        public ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
        {
            Options = opts;
            return ValueTask.FromResult<IMediaSession>(new Session());
        }
    }

    private sealed class Session : IMediaSession
    {
        public VideoDelivery Video => VideoDelivery.None;
        public void ConnectSignals(MediaSignalSink sink) { }
        public ValueTask PlayAsync() => ValueTask.CompletedTask;
        public ValueTask PauseAsync() => ValueTask.CompletedTask;
        public ValueTask SeekAsync(TimeSpan to, SeekMode mode) => ValueTask.CompletedTask;
        public void SetRate(double rate) { }
        public void SetVolume(double volume) { }
        public void SetMuted(bool muted) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
