using System.Collections.Generic;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A visible pop-out's image (<see cref="ImageCache.HiddenChildHeld"/>) that restarts while the cache is Deep must stay
/// resident when its decode lands, not be re-parked and evicted in the same pump.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class ImageCacheChildHeldLandingTests
{
    private const int Px = 64;

    [Fact]
    public void AChildHeldImageThatLandsWhileDeepStaysReadyAndKeepsItsTexture()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var cache = new ImageCache(new FakeImageDecoder(), budgetBytes: 1L << 30);
        cache.SetHeldImageSource(_ => { });
        var evicted = new List<int>();
        cache.SetEvictSink(evicted.Add);

        var h = cache.Request("https://covers.example/child-held", Px, Px);
        cache.Pin(h);
        cache.Pump();
        Assert.Equal(ImageState.Ready, cache.StateOf(h));

        cache.HiddenStage = HiddenStage.Deep;   // HiddenKeepLandings stays false: only the child-held set protects h
        Assert.Equal(1, cache.ParkPinnedGpu());
        Assert.True(cache.IsParked(h));
        evicted.Clear();

        cache.HiddenChildHeld = new HashSet<int> { h.Id };   // a visible pop-out now holds h
        cache.Pin(h);                                         // its pin restarts the parked entry
        cache.Pump();                                         // the fake decoder lands it here

        Assert.Equal(ImageState.Ready, cache.StateOf(h));
        Assert.False(cache.IsParked(h));
        Assert.Equal(0, cache.ParkedCount);
        Assert.DoesNotContain(h.Id, evicted);   // the texture the pop-out draws was not released on landing
    }
}
