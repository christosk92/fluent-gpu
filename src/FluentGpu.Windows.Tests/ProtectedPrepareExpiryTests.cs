using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// F007: a prepared protected session survives until the switch that wants it. The backend's expiry runs from the moment
/// the prefetch LANDED (not from creation, so a slow download does not age the session it is still building), an open
/// and <see cref="ProtectedMediaBackend.TryPeekPrepared"/> apply the same rule (so a host reports "warm" only for a
/// switch the open will actually serve), and an expired session is disposed rather than left holding its store. The
/// backend's clock is injected, so nothing here waits out <see cref="ProtectedMediaBackend.PreparedExpiryMs"/>.
/// </summary>
public sealed class ProtectedPrepareExpiryTests : IAsyncDisposable
{
    private const string InitA = "https://cdn.test/a/init.mp4";
    private const string InitB = "https://cdn.test/b/init.mp4";
    private const int Limit = ProtectedMediaBackend.PreparedExpiryMs;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private readonly List<IAsyncDisposable> _cleanup = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable d in _cleanup) await d.DisposeAsync();
    }

    private static MediaSource SourceFor(string initUrl)
        => MediaSource.FromUri("https://cdn.test/manifest.mpd")
            .With(new DrmConfig(DrmSystem.PlayReady)
            {
                SourceDescriptor = new DashSourceDescriptor
                {
                    InitUrl = initUrl,
                    SegmentBaseUrl = initUrl[..(initUrl.LastIndexOf('/') + 1)],
                    SegmentPrefix = "seg-",
                    SegmentSuffix = ".m4s",
                    SegmentCount = 10,
                    SegmentLengthMs = 4_000,
                    DurationMs = 40_000,
                },
            });

    /// <summary>A backend over a recording factory and a clock the test advances by hand.</summary>
    private sealed class Rig
    {
        public readonly List<FakeProtectedVideoPlayer> Created = new();
        public readonly ProtectedMediaBackend Backend;
        public long Now = 1_000;
        public TaskCompletionSource? Hold;

        public Rig()
        {
            Backend = new ProtectedMediaBackend(req =>
            {
                var player = new FakeProtectedVideoPlayer { ReadyOnPrefetch = true, PrefetchResult = Hold };
                Created.Add(player);
                return player;
            });
            Backend.Clock = () => Now;
        }
    }

    private async Task<IPreparedItem> Prepared(Rig rig, string initUrl)
    {
        IPreparedItem item = await rig.Backend.PrepareAtAsync(SourceFor(initUrl), TimeSpan.FromSeconds(10), Ct);
        _cleanup.Add(item);
        return item;
    }

    private async Task<IProtectedVideoPlayer> OpenedPlayer(Rig rig, string initUrl)
    {
        IMediaSession session = await rig.Backend.OpenAsync(SourceFor(initUrl), new MediaOpenOptions(), Ct);
        _cleanup.Add(session);
        return Assert.IsType<ProtectedMediaSession>(session).Player;
    }

    [Fact]
    public async Task ALandedSession_IsHandedOver_UpToAndIncludingTheExpiryWindow()
    {
        var rig = new Rig();
        await Prepared(rig, InitA);

        rig.Now += Limit;                                    // exactly at the limit: still the switch's
        Assert.True(rig.Backend.TryPeekPrepared(InitA));
        Assert.Same(Assert.Single(rig.Created), await OpenedPlayer(rig, InitA));
        Assert.Equal(0, rig.Created[0].DisposeCalls);
    }

    [Fact]
    public async Task ASessionPastTheExpiryWindow_IsDisposed_AndTheOpenGoesCold()
    {
        var rig = new Rig();
        await Prepared(rig, InitA);
        FakeProtectedVideoPlayer prepared = Assert.Single(rig.Created);

        rig.Now += Limit + 1;
        IProtectedVideoPlayer opened = await OpenedPlayer(rig, InitA);

        Assert.NotSame(prepared, opened);
        Assert.Equal(2, rig.Created.Count);
        Assert.Equal(1, prepared.DisposeCalls);              // not left holding its store and feeder
    }

    [Fact]
    public async Task TheExpiryWindowStartsWhenThePrefetchLands_NotWhenThePrepareStarted()
    {
        var rig = new Rig { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        Task<IPreparedItem> pending = rig.Backend.PrepareAtAsync(SourceFor(InitA), TimeSpan.FromSeconds(10), Ct).AsTask();
        FakeProtectedVideoPlayer prepared = Assert.Single(rig.Created);

        rig.Now += Limit - 5_000;                            // a slow download: 25 s in the air
        Assert.True(rig.Backend.TryPeekPrepared(InitA));     // registered while in flight, so a load can adopt it
        rig.Hold.SetResult();
        _cleanup.Add(await pending.WaitAsync(Bound, Ct));

        rig.Now += Limit - 5_000;                            // 50 s after creation, 25 s after landing
        Assert.True(rig.Backend.TryPeekPrepared(InitA));
        Assert.Same(prepared, await OpenedPlayer(rig, InitA));
        Assert.Single(rig.Created);
    }

    [Fact]
    public async Task APrepareThatNeverLands_StillExpiresFromItsCreation()
    {
        var rig = new Rig { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        Task<IPreparedItem> pending = rig.Backend.PrepareAtAsync(SourceFor(InitA), TimeSpan.FromSeconds(10), Ct).AsTask();
        FakeProtectedVideoPlayer stalled = Assert.Single(rig.Created);

        rig.Now += Limit + 1;

        Assert.False(rig.Backend.TryPeekPrepared(InitA));
        Assert.Equal(1, stalled.DisposeCalls);
        rig.Hold.SetResult();                                // let the abandoned prepare finish so the test leaves nothing running
        _cleanup.Add(await pending.WaitAsync(Bound, Ct));
    }

    [Fact]
    public async Task Peek_AppliesTheOpensRule_AndNeverRefreshesTheWindow()
    {
        var rig = new Rig();
        Assert.False(rig.Backend.TryPeekPrepared(null));
        Assert.False(rig.Backend.TryPeekPrepared(InitA));    // nothing registered
        await Prepared(rig, InitA);

        rig.Now += Limit - 1_000;
        Assert.True(rig.Backend.TryPeekPrepared(InitA));
        Assert.False(rig.Backend.TryPeekPrepared(InitB));    // another rung's init url is not this session's
        rig.Now += 2_000;                                    // the peek above did not buy it more time
        Assert.False(rig.Backend.TryPeekPrepared(InitA));
        Assert.Equal(1, Assert.Single(rig.Created).DisposeCalls);
        Assert.False(rig.Backend.TryPeekPrepared(InitA));    // and it is gone, not merely reported gone
    }

    [Fact]
    public async Task Peek_IsFalseOnceTheOpenTookTheSession_OrItsItemWasDisposed()
    {
        var rig = new Rig();
        await Prepared(rig, InitA);
        await OpenedPlayer(rig, InitA);
        Assert.False(rig.Backend.TryPeekPrepared(InitA));    // taken exactly once

        IPreparedItem item = await Prepared(rig, InitB);
        Assert.True(rig.Backend.TryPeekPrepared(InitB));
        await item.DisposeAsync();
        Assert.False(rig.Backend.TryPeekPrepared(InitB));    // a disposed prepare is never reported warm
    }

    [Fact]
    public async Task APrepareOfTheSameInitUrl_ReplacesTheParkedOne_AndGetsAFreshWindow()
    {
        var rig = new Rig();
        await Prepared(rig, InitA);
        FakeProtectedVideoPlayer old = rig.Created[0];

        rig.Now += Limit + 1;                                // the parked one has expired: the host re-prepares
        await Prepared(rig, InitA);

        Assert.Equal(1, old.DisposeCalls);
        Assert.True(rig.Backend.TryPeekPrepared(InitA));
        Assert.Same(rig.Created[1], await OpenedPlayer(rig, InitA));
        Assert.Equal(2, rig.Created.Count);
    }
}
