using System.Collections.Generic;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The cache half of the Deep hidden stage: parking, restarting, the invariants every exit from None keeps, and the
/// keep-while-hidden count. No host, no device.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class ImageCacheDeepTests
{
    private const int Px = 64;

    private static (ImageCache cache, List<int> evicted, HashSet<int> held) Make()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var held = new HashSet<int>();
        var cache = new ImageCache(new FakeImageDecoder(), budgetBytes: 1L << 30);
        cache.SetHeldImageSource(set => set.UnionWith(held));
        var evicted = new List<int>();
        cache.SetEvictSink(evicted.Add);
        return (cache, evicted, held);
    }

    private static ImageHandle Land(ImageCache cache, int i, bool pin)
    {
        var h = cache.Request("https://covers.example/" + i, Px, Px);
        if (pin) cache.Pin(h);
        cache.Pump();
        return h;
    }

    [Fact]
    public void ParkingTakesExactlyThePinnedAndHeldReadyEntriesWithoutKeepOrChildHeld()
    {
        var (cache, evicted, held) = Make();
        var pinned = Land(cache, 1, pin: true);
        var kept = Land(cache, 2, pin: true);
        cache.AddKeepWhileHidden(kept, +1);
        var childHeld = Land(cache, 3, pin: true);
        var rowCell = Land(cache, 4, pin: false);
        held.Add(rowCell.Id);
        var loose = Land(cache, 5, pin: false);   // unpinned and unheld: Shallow's job, not a parked identity

        Assert.Equal(2, cache.ParkPinnedGpu(new HashSet<int> { childHeld.Id }));

        Assert.Equal(2, cache.ParkedCount);
        Assert.True(cache.IsParked(pinned));
        Assert.True(cache.IsParked(rowCell));
        Assert.Equal(ImageState.Ready, cache.StateOf(kept));
        Assert.Equal(ImageState.Ready, cache.StateOf(childHeld));
        Assert.Equal(ImageState.Ready, cache.StateOf(loose));
        Assert.Equal(ImageState.None, cache.StateOf(pinned));
        Assert.Equal((Px, Px), cache.SizeOf(pinned));   // layout never sees it leave
        Assert.Equal(new[] { pinned.Id, rowCell.Id }, Sorted(evicted));
    }

    [Fact]
    public void ARestoreRestartsEveryParkedEntryAndTrackedLandingsSnapWithNoFade()
    {
        var (cache, _, _) = Make();
        var a = Land(cache, 1, pin: true);
        var b = Land(cache, 2, pin: true);
        cache.HiddenStage = HiddenStage.Deep;
        cache.ParkPinnedGpu();
        cache.HiddenStage = HiddenStage.Visible;

        Assert.Equal(2, cache.RestartParked(new HashSet<int> { a.Id }));   // only a is on screen: b restarts untracked
        Assert.Equal(0, cache.ParkedCount);
        Assert.Equal(1, cache.RestorePendingCount);
        cache.Pump();
        Assert.Equal(0, cache.RestorePendingCount);
        Assert.Equal(ImageState.Ready, cache.StateOf(a));
        Assert.Equal(ImageState.Ready, cache.StateOf(b));
        Assert.Equal(1f, cache.CrossFadeOf(a));   // snapped: no reveal ran for the tracked landing
    }

    [Fact]
    public void ExpiringTheHoldReturnsLateLandingsToTheOrdinaryReveal()
    {
        var (cache, _, _) = Make();
        var a = Land(cache, 1, pin: true);
        cache.HiddenStage = HiddenStage.Deep;
        cache.ParkPinnedGpu();
        cache.HiddenStage = HiddenStage.Visible;
        cache.RestartParked();
        Assert.Equal(1, cache.RestorePendingCount);
        cache.ClearRestoreTracking();
        Assert.Equal(0, cache.RestorePendingCount);
        cache.Pump();
        Assert.Equal(ImageState.Ready, cache.StateOf(a));
        Assert.Equal(0, cache.RestorePendingCount);   // the late landing does not underflow the count
    }

    [Fact]
    public void ARequestOrPinOnAParkedEntryStaysParkedWhileDeepUnlessKeptOrChildHeld()
    {
        var (cache, _, _) = Make();
        var a = Land(cache, 1, pin: true);
        var b = Land(cache, 2, pin: true);
        cache.HiddenStage = HiddenStage.Deep;
        cache.ParkPinnedGpu();

        cache.Pin(a);                                        // a re-pin while Deep
        cache.Request("https://covers.example/1", Px, Px);    // a request while Deep
        cache.Pump();
        Assert.Equal(ImageState.None, cache.StateOf(a));
        Assert.True(cache.IsParked(a));

        cache.HiddenChildHeld = new HashSet<int> { b.Id };    // a visible pop-out now holds b
        cache.Pin(b);
        cache.Pump();
        Assert.False(cache.IsParked(b));
        Assert.Equal(1, cache.ParkedCount);
    }

    [Fact]
    public void ADecodeThatLandsWhileDeepIsReleasedAtOnceAndRestartsOnRestore()
    {
        var (cache, evicted, _) = Make();
        cache.HiddenStage = HiddenStage.Deep;
        var h = cache.Request("https://covers.example/9", Px, Px);   // the hidden window's own request, landing during Deep
        cache.Pin(h);
        cache.Pump();
        Assert.Equal(ImageState.None, cache.StateOf(h));
        Assert.True(cache.IsParked(h));
        Assert.Contains(h.Id, evicted);
        cache.HiddenStage = HiddenStage.Visible;
        Assert.Equal(1, cache.RestartParked());
        cache.Pump();
        Assert.Equal(ImageState.Ready, cache.StateOf(h));
        Assert.Equal(0, cache.ParkedCount);
    }

    [Fact]
    public void EveryExitFromNoneClearsParkedAndReclaimNeverLeavesTheCountStale()
    {
        var (cache, _, held) = Make();
        var a = Land(cache, 1, pin: true);
        cache.HiddenStage = HiddenStage.Deep;
        cache.ParkPinnedGpu();
        Assert.Equal(1, cache.ParkedCount);
        cache.HiddenStage = HiddenStage.Visible;
        cache.Pin(a);                 // a pin after the restore edge restarts through the ordinary path
        Assert.False(cache.IsParked(a));
        Assert.Equal(0, cache.ParkedCount);
        cache.Pump();
        Assert.Equal(ImageState.Ready, cache.StateOf(a));
        Assert.Empty(held);
    }

    [Fact]
    public void ADeviceLossWhileHiddenParksWhatIsHeldAndOnlyTombstonesTheRest()
    {
        var (cache, evicted, _) = Make();
        var pinned = Land(cache, 1, pin: true);
        var loose = Land(cache, 2, pin: false);
        cache.HiddenStage = HiddenStage.Shallow;
        cache.ReRealizeAllResident();
        Assert.True(cache.IsParked(pinned));
        Assert.False(cache.IsParked(loose));
        Assert.Equal(ImageState.None, cache.StateOf(loose));
        Assert.Empty(evicted);   // the store is gone: nothing to free
        cache.HiddenStage = HiddenStage.Visible;
        Assert.Equal(1, cache.RestartParked());
    }

    [Fact]
    public void KeepWhileHiddenCountsPerHandleSoASecondNodeCannotUnprotectItEarly()
    {
        var (cache, _, _) = Make();
        var h = Land(cache, 1, pin: true);
        cache.AddKeepWhileHidden(h, +1);
        cache.AddKeepWhileHidden(h, +1);
        cache.AddKeepWhileHidden(h, -1);
        Assert.True(cache.IsKeptWhileHidden(h));
        cache.ParkPinnedGpu();
        Assert.Equal(ImageState.Ready, cache.StateOf(h));
        cache.AddKeepWhileHidden(h, -1);
        Assert.False(cache.IsKeptWhileHidden(h));
        cache.AddKeepWhileHidden(h, -1);   // never negative
        Assert.False(cache.IsKeptWhileHidden(h));
    }

    private static int[] Sorted(List<int> ids) { var a = ids.ToArray(); System.Array.Sort(a); return a; }
}
