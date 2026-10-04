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
    public async Task Open_NotPaused_PostsPlayWithTheSource_OverwritingTheWarmEnginesLeftoverPause()
    {
        var engine = new FakeVideoEngine();
        var backend = new MfMediaPlayer(() => engine);
        var first = await backend
            .OpenAsync(MediaSource.FromUri("http://host/a.mp4"), new MediaOpenOptions { StartPaused = true }, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        // Dispose = ReturnEngine: a pause is left in the (last-wins) Transport slot, followed by the detach.
        await first.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var second = await backend
            .OpenAsync(MediaSource.FromUri("http://host/b.mp4"), new MediaOpenOptions { StartPaused = false }, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("http://host/b.mp4", engine.LastSetSourceUrl);
        Assert.True(engine.Commands.TryTakeTransport(out bool play));
        Assert.True(play);           // play intent rides with the SetSource, not the first post-metadata pump

        await second.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
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
    public async Task WarmEngine_IsTornDown_AfterItHasBeenIdleLongEnough()
    {
        // "Warm across many opens" used to mean warm FOREVER: once one video had played, the engine — and with it a
        // second D3D11 device, an MF engine and whatever decode surfaces MF kept — stayed resident for the rest of the
        // session, invisible to every memory instrument the app has. A returned engine is now torn down once it has
        // been idle long enough that nothing is plausibly about to re-lease it.
        var engines = new List<FakeVideoEngine>();
        var backend = new MfMediaPlayer(() => { var e = new FakeVideoEngine(); engines.Add(e); return e; }, warmIdleMs: 30);

        var session = await backend
            .OpenAsync(MediaSource.FromUri("http://host/a.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engines[0].DisposeCalls);   // returned warm, not destroyed

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (engines[0].DisposeCalls == 0 && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.Equal(1, engines[0].DisposeCalls);
    }

    [Fact]
    public async Task WarmEngine_SurvivesATrackSkip_BecauseTheNextLeaseCancelsTheTeardown()
    {
        // The whole point of the warmth: a skip re-leases within seconds and must never pay a rebuild. The idle timer
        // is armed on return, so the guard is that a lease CANCELS it rather than racing it.
        int factoryCalls = 0;
        var engines = new List<FakeVideoEngine>();
        var backend = new MfMediaPlayer(() => { var e = new FakeVideoEngine(); factoryCalls++; engines.Add(e); return e; }, warmIdleMs: 5_000);

        var s1 = await backend.OpenAsync(MediaSource.FromUri("http://host/a.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await s1.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var s2 = await backend.OpenAsync(MediaSource.FromUri("http://host/b.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, factoryCalls);              // still the same engine — the skip paid no rebuild
        Assert.Equal(0, engines[0].DisposeCalls);
        await s2.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
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
        // The discarded engine is torn down off the lock on the pool (LeaseEngine never blocks on its dispose).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (engines[0].DisposeCalls == 0 && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
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

    // ── a slow engine dispose never runs on the caller or under the lease lock (F062) ───────────────────────────────

    [Fact]
    public async Task Open_DiscardingAnIdleFaultedEngine_DoesNotWaitForItsSlowDispose()
    {
        using var release = new ManualResetEventSlim(false);
        var engines = new List<FakeVideoEngine>();
        var backend = new MfMediaPlayer(() => { var e = new FakeVideoEngine(); engines.Add(e); return e; });

        var session1 = await backend
            .OpenAsync(MediaSource.FromUri("http://host/a.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        engines[0].Faulted = true;
        await session1.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));   // returned warm, now idle and faulted
        engines[0].OnDispose = () => release.Wait(TimeSpan.FromSeconds(10));   // the 2 s join + MFShutdown, modelled

        // The lease that discards the faulted engine must return while that engine's dispose is still blocked: the
        // dispose is on the pool, not on this thread and not under the lock this call (and every later lease) needs.
        var session2 = await backend
            .OpenAsync(MediaSource.FromUri("http://host/b.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engines[0].DisposeCalls);   // still inside its blocked dispose

        release.Set();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (engines[0].DisposeCalls == 0 && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.Equal(1, engines[0].DisposeCalls);

        await session2.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Open_WhileTheLeasedWarmEngineIsFaulted_TakesTheFreshWarmEngine_AndDisposesTheOrphanOnce()
    {
        // Device removal faults the warm engine MID-SESSION (F059), then a second open arrives before that session is
        // disposed. The rebuild must hand the fresh warm engine to this lease (no extra throwaway), clear the lease flag
        // that belonged to the replaced engine, and leave the orphan to its own session to dispose — exactly once.
        using var release = new ManualResetEventSlim(false);
        int factoryCalls = 0;
        var engines = new List<FakeVideoEngine>();
        var backend = new MfMediaPlayer(() => { var e = new FakeVideoEngine(); factoryCalls++; engines.Add(e); return e; });

        var session1 = await backend
            .OpenAsync(MediaSource.FromUri("http://host/a.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        engines[0].Faulted = true;
        engines[0].OnDispose = () => release.Wait(TimeSpan.FromSeconds(10));   // the 2 s join + MFShutdown, modelled

        var session2 = await backend
            .OpenAsync(MediaSource.FromUri("http://host/b.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, factoryCalls);                   // the fresh warm engine served the open: no extra engine
        Assert.Equal(1, engines[1].PostSetSourceCalls);
        Assert.Equal(0, engines[0].DisposeCalls);        // still leased by session1: left to that session, not disposed here

        // The orphan's dispose (blocked) must not stall its session's dispose, which is a return on the caller.
        await session1.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engines[0].DisposeCalls);
        Assert.Equal(0, engines[1].PostDetachCalls);     // the orphan's return never touched the fresh warm engine

        // The fresh warm engine's lease is its own: session2's dispose returns it warm.
        await session2.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, engines[1].PostDetachCalls);

        release.Set();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (engines[0].DisposeCalls == 0 && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.Equal(1, engines[0].DisposeCalls);

        // The lease flag is not stuck: a third open reuses the warm engine with no new engine.
        var session3 = await backend
            .OpenAsync(MediaSource.FromUri("http://host/c.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, factoryCalls);
        Assert.Equal(2, engines[1].PostSetSourceCalls);
        await session3.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Task.Delay(100);                           // let any (wrong) late dispose land before the final count
        Assert.Equal(1, engines[0].DisposeCalls);
    }

    [Fact]
    public async Task Dispose_OfAThrowawaySession_DoesNotWaitForTheEnginesSlowDispose()
    {
        using var release = new ManualResetEventSlim(false);
        var engines = new List<FakeVideoEngine>();
        var backend = new MfMediaPlayer(() => { var e = new FakeVideoEngine(); engines.Add(e); return e; });

        var session1 = await backend
            .OpenAsync(MediaSource.FromUri("http://host/a.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var session2 = await backend   // second CONCURRENT lease: a throwaway engine
            .OpenAsync(MediaSource.FromUri("http://host/b.mp4"), new MediaOpenOptions(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        engines[1].OnDispose = () => release.Wait(TimeSpan.FromSeconds(10));

        // ReturnEngine disposes the throwaway; the session's dispose (any thread, possibly the UI thread) must not block on it.
        await session2.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engines[1].DisposeCalls);

        // And the lease lock was free the whole time: a return of the warm session goes straight through.
        await session1.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, engines[0].PostDetachCalls);

        release.Set();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (engines[1].DisposeCalls == 0 && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.Equal(1, engines[1].DisposeCalls);
    }
}
