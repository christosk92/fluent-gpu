using System.Collections.Generic;
using System.Linq;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The pure hidden-window stage machine, its tunables and its command-line parser. Touches the process-wide
/// <see cref="HiddenMemoryBudget"/>, so it shares the serial collection and restores the defaults.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class HiddenMemoryPolicyTests : System.IDisposable
{
    public HiddenMemoryPolicyTests() => HiddenMemoryBudget.Reset();
    public void Dispose() => HiddenMemoryBudget.Reset();

    [Fact]
    public void NoStageBeforeTheDelayAndShallowAtIt()
    {
        var p = new HiddenMemoryPolicy();
        Assert.Null(p.Advance(HiddenPark.Os, 1_000));
        Assert.Null(p.Advance(HiddenPark.Os, 2_999));
        Assert.Equal(HiddenStage.Visible, p.Stage);
        Assert.Equal(HiddenStage.Shallow, p.Advance(HiddenPark.Os, 3_000));
        Assert.Null(p.Advance(HiddenPark.Os, 90_000));   // no further stage, no repeated edge
    }

    [Fact]
    public void AnUnparkReturnsToVisibleAtOnceAndAShortFlapNeverLeavesVisible()
    {
        var p = new HiddenMemoryPolicy();
        p.Advance(HiddenPark.Os, 0);
        Assert.Null(p.Advance(HiddenPark.Os, 1_900));
        Assert.Null(p.Advance(HiddenPark.None, 1_950));   // a 1.95 s park: nothing was ever released
        p.Advance(HiddenPark.Os, 2_000);                  // the timer restarts from the NEW park edge
        Assert.Null(p.Advance(HiddenPark.Os, 3_900));
        Assert.Equal(HiddenStage.Shallow, p.Advance(HiddenPark.Os, 4_000));
        Assert.Equal(HiddenStage.Visible, p.Advance(HiddenPark.None, 4_001));   // the restore edge
    }

    [Fact]
    public void ACoverParkWaitsMuchLongerThanAMinimize()
    {
        var p = new HiddenMemoryPolicy();
        p.Advance(HiddenPark.Cover, 0);
        Assert.Null(p.Advance(HiddenPark.Cover, 29_999));
        Assert.Equal(HiddenStage.Shallow, p.Advance(HiddenPark.Cover, 30_000));
    }

    [Fact]
    public void ACoverParkThatBecomesAMinimizeUsesTheShorterDelayOnTheAccruedTime()
    {
        var p = new HiddenMemoryPolicy();
        p.Advance(HiddenPark.Cover, 0);
        Assert.Null(p.Advance(HiddenPark.Cover, 1_000));
        Assert.Equal(HiddenStage.Shallow, p.Advance(HiddenPark.Os, 2_500));
    }

    [Fact]
    public void NextDueReportsTheRemainingWaitThenNothing()
    {
        var p = new HiddenMemoryPolicy();
        Assert.Equal(-1, p.NextDueInMs(HiddenPark.None, 0));
        p.Advance(HiddenPark.Os, 100);
        Assert.Equal(2_000, p.NextDueInMs(HiddenPark.Os, 100));
        Assert.Equal(500, p.NextDueInMs(HiddenPark.Os, 1_600));
        Assert.Equal(0, p.NextDueInMs(HiddenPark.Os, 9_000));
        p.Advance(HiddenPark.Os, 2_100);
        Assert.Equal(-1, p.NextDueInMs(HiddenPark.Os, 2_100));   // reached: the parked loop blocks again
    }

    [Fact]
    public void MaxDisablesTheStageAndANonPositiveDelayReleasesOnTheEdge()
    {
        Assert.True(HiddenMemoryBudget.TryApply("max"));
        var never = new HiddenMemoryPolicy();
        never.Advance(HiddenPark.Os, 0);
        Assert.Null(never.Advance(HiddenPark.Os, long.MaxValue / 2));
        Assert.Equal(-1, never.NextDueInMs(HiddenPark.Os, 0));

        Assert.True(HiddenMemoryBudget.TryApply("0"));
        var now = new HiddenMemoryPolicy();
        Assert.Equal(HiddenStage.Shallow, now.Advance(HiddenPark.Os, 50));
    }

    [Fact]
    public void TheSwitchParsesBothDelaysAndRejectsGarbageWithoutChangingAnything()
    {
        Assert.True(HiddenMemoryBudget.TryApply("1500:45000"));
        Assert.Equal(1_500, HiddenMemoryBudget.ShallowDelayMs);
        Assert.Equal(45_000, HiddenMemoryBudget.CoverShallowDelayMs);
        Assert.False(HiddenMemoryBudget.TryApply("soon"));
        Assert.False(HiddenMemoryBudget.TryApply("1:2:3"));
        Assert.False(HiddenMemoryBudget.TryApply("100:never"));
        Assert.Equal(1_500, HiddenMemoryBudget.ShallowDelayMs);
        Assert.Equal(45_000, HiddenMemoryBudget.CoverShallowDelayMs);
        EngineSwitches.ApplyList("hidden=max:max");
        Assert.Equal(long.MaxValue, HiddenMemoryBudget.ShallowDelayMs);
        Assert.Equal(long.MaxValue, HiddenMemoryBudget.CoverShallowDelayMs);
    }

    // ── ImageCache.ReleaseUnpinnedGpu ───────────────────────────────────────────────────────────────────────

    private const int Px = 64;

    private static (ImageCache cache, List<int> evicted) Cache(HashSet<int> held)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var cache = new ImageCache(new FakeImageDecoder(), budgetBytes: 1L << 30);
        cache.SetHeldImageSource(set => set.UnionWith(held));
        var evicted = new List<int>();
        cache.SetEvictSink(evicted.Add);
        return (cache, evicted);
    }

    private static ImageHandle Land(ImageCache cache, int i, bool pin)
    {
        var h = cache.Request("https://covers.example/" + i, Px, Px);
        if (pin) cache.Pin(h);
        cache.Pump();
        return h;
    }

    [Fact]
    public void ReleaseUnpinnedGpuEvictsExactlyTheUnpinnedUnheldReadySet()
    {
        var held = new HashSet<int>();
        var (cache, evicted) = Cache(held);
        var pinned = Land(cache, 1, pin: true);
        var loose1 = Land(cache, 2, pin: false);
        var loose2 = Land(cache, 3, pin: false);
        var rowCell = Land(cache, 4, pin: false);      // on screen without a pin: a list row's image cell
        held.Add(rowCell.Id);
        Assert.Equal(4, cache.ReadyCount);

        Assert.Equal(2, cache.ReleaseUnpinnedGpu());

        Assert.Equal(ImageState.Ready, cache.StateOf(pinned));
        Assert.Equal(ImageState.Ready, cache.StateOf(rowCell));
        Assert.NotEqual(ImageState.Ready, cache.StateOf(loose1));
        Assert.NotEqual(ImageState.Ready, cache.StateOf(loose2));
        Assert.Equal(2, cache.ReadyCount);
        Assert.Equal(new[] { loose1.Id, loose2.Id }.OrderBy(x => x), evicted.OrderBy(x => x));   // each through the evict sink, once
        Assert.Equal(0, cache.ReleaseUnpinnedGpu());                                              // idempotent
    }

    [Fact]
    public void AReleasedImageIsRecoveredByTheNextRequest()
    {
        var (cache, _) = Cache(new HashSet<int>());
        var h = Land(cache, 7, pin: false);
        cache.ReleaseUnpinnedGpu();
        Assert.NotEqual(ImageState.Ready, cache.StateOf(h));
        cache.Pin(h);                                      // the row realizes again
        cache.Pump();
        Assert.Equal(ImageState.Ready, cache.StateOf(h));
    }

    [Fact]
    public void WithoutAHeldImageSourceNothingIsReleased()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var cache = new ImageCache(new FakeImageDecoder(), budgetBytes: 1L << 30);
        var h = cache.Request("https://covers.example/9", Px, Px);
        cache.Pump();
        Assert.Equal(0, cache.ReleaseUnpinnedGpu());
        Assert.Equal(ImageState.Ready, cache.StateOf(h));
    }
}
