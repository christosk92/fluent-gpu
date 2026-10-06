using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The window-relative image budget and the measured committed-bytes charge. Both are accounting: neither may make a cache
/// hold FEWER images than it does today.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class ImageBudgetTests
{
    private const long MiB = 1L << 20;

    [Theory]
    [InlineData(40 * MiB, 800, 600, true)]
    [InlineData(64 * MiB, 3840, 2160, true)]
    [InlineData(64 * MiB, 3840, 2160, false)]
    [InlineData(96 * MiB, 1920, 1080, false)]
    [InlineData(64 * MiB, 100, 100, false)]
    public void TheBudgetIsNeverBelowWhatTheCacheWasBuiltWith(long baseBudget, int w, int h, bool weak)
        => Assert.True(ImageBudget.Current(baseBudget, w, h, weak) >= baseBudget);

    [Fact]
    public void ABiggerWindowEarnsMoreUpToTheTierCeiling()
    {
        Assert.Equal(ImageBudget.Floor, ImageBudget.Derived(320, 240, weak: false));
        Assert.Equal(ImageBudget.Ceiling, ImageBudget.Derived(7680, 4320, weak: false));
        Assert.Equal(ImageBudget.WeakCeiling, ImageBudget.Derived(7680, 4320, weak: true));
        long fhd = ImageBudget.Derived(1920, 1080, weak: false);
        Assert.InRange(fhd, 24 * MiB, ImageBudget.Ceiling);
        Assert.Equal(64 * MiB, ImageBudget.Current(64 * MiB, 1920, 1080, weak: false));   // 3 x 8 MiB: today's 64 already covers it
        Assert.Equal(ImageBudget.Ceiling, ImageBudget.Current(64 * MiB, 7680, 4320, weak: false));   // an 8K window earns the ceiling
        Assert.Equal(ImageBudget.Derived(3840, 2160, weak: false), ImageBudget.Current(64 * MiB, 3840, 2160, weak: false));   // 4K: three window-areas, above 64 MiB
    }

    [Fact]
    public void ABaseBelowTheFloorIsAnExplicitChoiceAndStaysAsGiven()
        => Assert.Equal(8 * MiB, ImageBudget.Current(8 * MiB, 3840, 2160, weak: false));

    private static int ResidentUnpinned256(ImageCache cache, int count)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var handles = new ImageHandle[count];
        for (int i = 0; i < count; i++)
        {
            handles[i] = cache.Request("https://covers.example/" + i, 256, 256);
            cache.Pump();
        }
        int ready = 0;
        foreach (var h in handles) if (cache.StateOf(h) == ImageState.Ready) ready++;
        return ready;
    }

    [Fact]
    public void ACorrectedChargeScalesTheBudgetSoTheCapacityInCoversDoesNotShrink()
    {
        const long budget = 10 * 262_144 + 1_000;   // room for ten 256 x 256 covers at the formula's 256 KiB
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var plain = new ImageCache(new FakeImageDecoder(), budgetBytes: budget);
        int before = ResidentUnpinned256(plain, 14);

        var measured = new ImageCache(new FakeImageDecoder(), budgetBytes: budget);
        measured.SetMeasuredCommittedBytes(256, 320 * 1024);   // what the Adreno really commits for one
        Assert.Equal(320 * 1024, measured.CommittedBytesOf(256, 256));
        int after = ResidentUnpinned256(measured, 14);

        Assert.Equal(10, before);
        Assert.Equal(before, after);
        Assert.True(measured.BudgetBytes > plain.BudgetBytes);
    }

    [Fact]
    public void AnUnmeasuredBucketKeepsTheFormula()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var cache = new ImageCache(new FakeImageDecoder());
        cache.SetMeasuredCommittedBytes(256, 320 * 1024);
        Assert.Equal(ImageCache.CommittedBytesFor(128, 128), cache.CommittedBytesOf(128, 128));
        Assert.Equal(ImageCache.CommittedBytesFor(700, 300), cache.CommittedBytesOf(700, 300));   // oversize: committed at its own size
    }

    [Fact]
    public void TheWindowBudgetNeverLowersAnExistingCapAndAMeasurementComposesWithIt()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var cache = new ImageCache(new FakeImageDecoder(), budgetBytes: 64 * MiB);
        cache.SetWindowBudget(320, 240);
        Assert.Equal(64 * MiB, cache.BudgetBytes);
        cache.SetWindowBudget(7680, 4320);
        Assert.Equal(96 * MiB, cache.BudgetBytes);
        cache.SetMeasuredCommittedBytes(256, 320 * 1024);
        Assert.Equal(120 * MiB, cache.BudgetBytes);   // 96 MiB x 1.25
        cache.SetWindowBudget(320, 240);
        Assert.Equal(80 * MiB, cache.BudgetBytes);    // 64 MiB x 1.25: back to today's, scaled
    }
}
