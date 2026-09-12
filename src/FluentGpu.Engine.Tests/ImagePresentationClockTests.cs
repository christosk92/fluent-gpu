using FluentGpu.Hosting;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class ImagePresentationClockTests
{
    [Fact]
    public void SparsePublicationsCannotRestartACompletedReveal()
    {
        var clock = new ImagePresentationClock();
        double stamp = 10_000;
        float anchor = (float)clock.Sample(stamp);
        float previous = 0;
        var snapshot = new ImageRecordingSnapshot();
        snapshot.Add(1, ImageState.Ready, 16, 16, 0, 150, 0);
        for (int elapsed = 0; elapsed <= 1000; elapsed += 10)
        {
            // An idle UI posts at 200ms intervals; animation-time Resync may return zero on every such frame.
            // Image time samples real elapsed time independently of that deliberately resynced animation clock.
            if (elapsed % 200 == 0)
            {
                stamp = 10_000 + elapsed;
                anchor = (float)clock.Sample(stamp);
            }
            float now = ImagePresentationClock.Extrapolate(anchor, stamp, 10_000 + elapsed);
            float opacity = ImageCache.ResolveFade(now, 0, 150, 0);
            Assert.True(opacity >= previous, $"fade rewound at {elapsed}ms: {previous} -> {opacity}");
            if (elapsed >= 150)
            {
                Assert.Equal(1f, opacity);
                Assert.False(snapshot.HasCrossfades(now));
            }
            previous = opacity;
        }
    }

    [Fact]
    public void HiddenTimeFinishesARevealRegardlessOfRestorePublicationOrdering()
    {
        var clock = new ImagePresentationClock();
        Assert.Equal(0, clock.Sample(1000));
        Assert.Equal(50, clock.Sample(1050));
        // No frames are needed while hidden. Old and fresh publications agree at restore, even if the
        // render consumer adopts the fresh publication before observing the window's visibility edge.
        float old = ImagePresentationClock.Extrapolate(50, 1050, 51_010);
        float fresh = (float)clock.Sample(51_010);
        Assert.Equal(50_010f, old);
        Assert.Equal(old, fresh);
        Assert.Equal(1f, ImageCache.ResolveFade(fresh, 0, 150, 0));
    }

    [Fact]
    public void NewRevealAfterResumeHasItsFullDuration()
    {
        var cache = new ImageCache(new FakeImageDecoder());
        cache.AdvancePresentationClock(1000);
        var handle = cache.Request("late-completion", 16, 16);
        cache.AdvancePresentationClock(100_010); // same ordering as the idle/hidden Pump path
        cache.Pump();
        Assert.Equal(0f, cache.CrossFadeOf(handle));
        cache.AdvancePresentationClock(100_020);
        Assert.InRange(cache.CrossFadeOf(handle), 0.001f, .99f);
        cache.AdvancePresentationClock(100_330);
        Assert.Equal(1f, cache.CrossFadeOf(handle));
    }

    [Fact]
    public void PopoutUsesTheSameCacheClockDomainAndSnapshotTimestamp()
    {
        var cache = new ImageCache(new FakeImageDecoder());
        cache.AdvancePresentationClock(1000);
        cache.AdvancePresentationClock(61_000); // parent has been open a minute
        var parent = new ImageRecordingSnapshot();
        parent.Capture(cache);
        float parentAnchor = cache.ClockMs;
        // The popout receives this cache; it must not create a new zero-origin clock.
        cache.AdvancePresentationClock(61_010);
        var child = new ImageRecordingSnapshot();
        child.Capture(cache);
        Assert.Equal(60_010f, cache.ClockMs);
        Assert.Equal(61_010d, child.ClockCapturedAtMs);
        Assert.Equal(cache.ClockMs, ImagePresentationClock.Extrapolate(parentAnchor, parent.ClockCapturedAtMs, 61_010));
        cache.AdvancePresentationClock(61_020); // next parent paint stays in the same domain
        Assert.Equal(60_020f, cache.ClockMs);
    }
}
