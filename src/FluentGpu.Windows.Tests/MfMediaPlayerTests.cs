using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>Tests for the <see cref="MfMediaPlayer"/> backend v2 (URL resolution, capability reporting, the warm-engine
/// lease/return pool, error routing) driven through an injected <see cref="FakeVideoEngine"/> factory — no real MF
/// engine.</summary>
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
        Assert.Equal(1, engine.StartCalls);
        Assert.Equal(1, engine.PostSetSourceCalls);
        Assert.Equal("http://host/clip.mp4", engine.LastSetSourceUrl);
        Assert.True(engine.Commands.TryTakeLoop(out bool loop));
        Assert.False(loop);          // a media element does not loop by default
        Assert.True(engine.Commands.TryTakeTransport(out bool play));
        Assert.False(play);          // StartPaused ⇒ paused after open

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OpenAsync_NeverThrowsForAnMfOpenFailure()
    {
        // PostSetSource is fire-and-forget: there is no more blocking bring-up/SetSource HRESULT for OpenAsync to
        // check synchronously. A failure can only ever surface later, as a typed MediaError on the session's own
        // signal sink (MfMediaSessionTests.EngineError_MapsToTypedMediaError_AndFails covers that mapping).
        var engine = new FakeVideoEngine();
        var backend = new MfMediaPlayer(() => engine);

        IMediaSession session = await backend
            .OpenAsync(MediaSource.FromUri("http://host/bad.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<MfMediaSession>(session);
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Open_UnsupportedSource_Throws()
    {
        var backend = new MfMediaPlayer(() => new FakeVideoEngine());
        await Assert.ThrowsAsync<NotSupportedException>(() => backend
            .OpenAsync(MediaSource.FromBytes(new byte[] { 1 }), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ── warm-engine lease/return (plan §1.5 / §7 required coverage (c)) ─────────────────────────────────────────────

    [Fact]
    public async Task Open_ReusesTheWarmEngine_AcrossSuccessiveOpens()
    {
        int factoryCalls = 0;
        var engines = new List<FakeVideoEngine>();
        var backend = new MfMediaPlayer(() => { var e = new FakeVideoEngine(); factoryCalls++; engines.Add(e); return e; });

        var session1 = await backend
            .OpenAsync(MediaSource.FromUri("http://host/a.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        // Disposing the session RETURNS the engine warm to the pool (pause + detach — no teardown).
        await session1.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var session2 = await backend
            .OpenAsync(MediaSource.FromUri("http://host/b.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, factoryCalls);                    // ONE engine instance served both opens — no rebuild
        Assert.Equal(2, engines[0].PostSetSourceCalls);    // both opens posted SetSource on the SAME warm engine
        Assert.Equal(1, engines[0].PostDetachCalls);       // session1's dispose returned it warm

        await session2.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Open_RebuildsTheEngine_WhenTheWarmOneIsFaulted()
    {
        int factoryCalls = 0;
        var engines = new List<FakeVideoEngine>();
        var backend = new MfMediaPlayer(() => { var e = new FakeVideoEngine(); factoryCalls++; engines.Add(e); return e; });

        var session1 = await backend
            .OpenAsync(MediaSource.FromUri("http://host/a.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        engines[0].Faulted = true;   // bring-up failed sometime after the lease
        await session1.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var session2 = await backend
            .OpenAsync(MediaSource.FromUri("http://host/b.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, factoryCalls);                  // the Faulted engine was discarded; a fresh one was built
        Assert.Equal(1, engines[0].DisposeCalls);        // the old (faulted) engine was actually torn down
        Assert.Equal(1, engines[1].PostSetSourceCalls);  // the NEW engine served the second open

        await session2.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Open_SecondConcurrentLease_GetsAThrowawayEngine()
    {
        // Two MediaPlayers sharing one MfMediaPlayer concurrently is an edge case, not the steady state: the second
        // lease before the first session is disposed must not contend for the warm engine's single-writer ownership.
        int factoryCalls = 0;
        var backend = new MfMediaPlayer(() => { factoryCalls++; return new FakeVideoEngine(); });

        var session1 = await backend
            .OpenAsync(MediaSource.FromUri("http://host/a.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var session2 = await backend
            .OpenAsync(MediaSource.FromUri("http://host/b.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, factoryCalls);   // the warm engine + one throwaway

        await session1.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await session2.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
