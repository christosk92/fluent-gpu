using System.Collections.Generic;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The image cache over a long session. Scrolling a 100k-row list with a unique cover per row requests, shows, unpins
/// and evicts tens of thousands of distinct images. Two defects made that session's cost grow with every image EVER
/// seen: evicted entries stayed in the cache forever as tombstones (so a retained node could re-pin its handle), and
/// every LRU eviction scanned all of them. Now a tombstone nothing HOLDS any more (no scene node, row cell or
/// hold-last-good target — the host's enumeration) is reclaimed, and eviction walks an indexed LRU list from its head.
/// </summary>
public sealed class ImageCacheReclaimTests
{
    private const int Px = 64;
    private static readonly long OneImage = ImageCache.CommittedBytesFor(Px, Px);   // what one cover commits (64 KiB placement)

    private static ImageCache Cache(int budgetImages, HashSet<int>? held = null)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var cache = new ImageCache(new FakeImageDecoder(), budgetBytes: budgetImages * OneImage);
        cache.SetHeldImageSource(set => { if (held is not null) set.UnionWith(held); });
        return cache;
    }

    /// <summary>Show one image the way a realized row does: request, pin while on screen, land, unpin as it scrolls off.</summary>
    private static ImageHandle ShowAndScrollPast(ImageCache cache, int i)
    {
        var h = cache.Request("https://covers.example/" + i, Px, Px);
        cache.Pin(h);
        cache.Pump();
        cache.Unpin(h);
        return h;
    }

    [Fact]
    public void TombstonesNothingHoldsAreReclaimed_TheEntryCountTracksTheLiveSet()
    {
        var cache = Cache(budgetImages: 20);
        for (int i = 0; i < 3000; i++) ShowAndScrollPast(cache, i);
        cache.Pump();
        // The live set is the ~20 resident images; everything else was evicted and is held by nothing.
        Assert.True(cache.EntryCount <= 2048, $"entries={cache.EntryCount} after 3000 distinct images (budget 20)");
    }

    [Fact]
    public void AHeldTombstoneIsNeverReclaimed_AndItsNodeCanStillRePinIt()
    {
        var held = new HashSet<int>();
        var cache = Cache(budgetImages: 20, held);
        var kept = ShowAndScrollPast(cache, -1);
        held.Add(kept.Id);                                   // a parked node still carries this id in its paint
        for (int i = 0; i < 3000; i++) ShowAndScrollPast(cache, i);
        cache.Pump();
        Assert.NotEqual(ImageState.Ready, cache.StateOf(kept));   // evicted long ago…
        cache.Pin(kept);                                          // …the node unparks and re-pins its handle
        cache.Pump();
        Assert.Equal(ImageState.Ready, cache.StateOf(kept));      // and recovers without its URL
    }

    [Fact]
    public void EvictionCostIsIndependentOfHowManyImagesWereEverSeen()
    {
        var cache = Cache(budgetImages: 20, held: null);
        for (int i = 0; i < 3000; i++) ShowAndScrollPast(cache, i);
        Assert.True(cache.LastEvictVisited <= 4, $"the last eviction examined {cache.LastEvictVisited} entries");
    }

    [Fact]
    public void EvictionOrderIsLeastRecentlyUsed()
    {
        var cache = Cache(budgetImages: 3);
        var a = ShowAndScrollPast(cache, 1);
        var b = ShowAndScrollPast(cache, 2);
        var c = ShowAndScrollPast(cache, 3);
        cache.Request("https://covers.example/1", Px, Px);   // A is used again (a row re-renders it)
        ShowAndScrollPast(cache, 4);                          // over budget by one: the LRU entry goes
        Assert.Equal(ImageState.Ready, cache.StateOf(a));
        Assert.NotEqual(ImageState.Ready, cache.StateOf(b));
        Assert.Equal(ImageState.Ready, cache.StateOf(c));
    }
}
