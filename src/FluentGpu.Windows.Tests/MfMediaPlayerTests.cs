using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>M1 tests for the <see cref="MfMediaPlayer"/> backend (URL resolution, capability reporting, open lifecycle,
/// error mapping) driven through an injected <see cref="FakeVideoEngine"/> factory — no real MF engine.</summary>
public sealed class MfMediaPlayerTests
{
    [Fact]
    public void Capabilities_ReportVideoCapable()
    {
        var caps = new MfMediaPlayer().Capabilities;
        Assert.True(caps.SupportsVideo);
        Assert.False(caps.SupportsAudioGraph);
        Assert.NotNull(caps.IsSupported);
        Assert.True(caps.IsSupported!(new MediaContentType(Container.Mp4, CodecId.H264, CodecId.Aac)));
    }

    [Theory]
    [InlineData("http://host/clip.mp4", "http://host/clip.mp4")]
    public void ResolveUrl_Uri(string url, string expected)
        => Assert.Equal(expected, MfMediaPlayer.ResolveUrl(MediaSource.FromUri(url)));

    [Fact]
    public void ResolveUrl_FileAndUnsupported()
    {
        Assert.Equal("C:/media/clip.mp4", MfMediaPlayer.ResolveUrl(MediaSource.FromFile("C:/media/clip.mp4")));
        Assert.Null(MfMediaPlayer.ResolveUrl(MediaSource.FromBytes(new byte[] { 1, 2, 3 })));
    }

    [Fact]
    public async Task Open_BuildsSession_HonorsStartPaused_AndDisablesLoop()
    {
        var engine = new FakeVideoEngine();
        var backend = new MfMediaPlayer(() => engine);

        var session = await backend
            .OpenAsync(MediaSource.FromUri("http://host/clip.mp4"), new MediaOpenOptions { StartPaused = true }, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<MfMediaSession>(session);
        Assert.Equal(1, engine.InitializeCalls);
        Assert.False(engine.LastLoop);          // a media element does not loop by default
        Assert.True(engine.PauseCalls >= 1);    // StartPaused ⇒ paused after open

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Open_InitFailure_ThrowsAndDisposesEngine()
    {
        var engine = new FakeVideoEngine { InitializeResult = unchecked((int)0x80004005) };
        var backend = new MfMediaPlayer(() => engine);

        await Assert.ThrowsAsync<InvalidOperationException>(() => backend
            .OpenAsync(MediaSource.FromUri("http://host/bad.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, engine.DisposeCalls);
    }

    [Fact]
    public async Task Open_UnsupportedSource_Throws()
    {
        var backend = new MfMediaPlayer(() => new FakeVideoEngine());
        await Assert.ThrowsAsync<NotSupportedException>(() => backend
            .OpenAsync(MediaSource.FromBytes(new byte[] { 1 }), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
