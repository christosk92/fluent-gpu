using System;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E22: the retained-tile caps follow the window area. The fixed 64 tile surfaces / 128 MiB ceiling are the FLOORS (every
/// window up to 4K at 100 % keeps today's values); a 7680 x 4320 px window (3840 x 2160 DIP at 200 %) raises both so its
/// visible tiles stay resident and tiled instead of degrading to direct, untiled drawing. Serial: the budget tunables are
/// process-wide statics.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class TileBudgetScaleTests
{
    private const long Mib = TileBudget.MiB;

    private sealed class EmptyRoot : FluentGpu.Hooks.Component
    {
        public override FluentGpu.Dsl.Element Render() => FluentGpu.Dsl.Ui.VStack(0);
    }

    public TileBudgetScaleTests() => TileBudget.ResetToDefaults();

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1024, 512, 4)]       // 1 x 1 tile + a phase column and row
    [InlineData(1920, 1080, 12)]     // 2 -> 3 columns, 3 -> 4 rows
    [InlineData(3840, 2160, 30)]     // 4 -> 5, 5 -> 6
    [InlineData(7680, 4320, 90)]     // 8 -> 9, 9 -> 10
    public void CoverTiles_CountsTheTilesAWindowTouches(int w, int h, int expected)
        => Assert.Equal(expected, TileBudget.CoverTiles(w, h));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(800, 600)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1600)]
    [InlineData(3840, 2160)]         // 4K at 100 %: today's cap still covers it
    public void TheSurfaceCap_KeepsTodaysFloor_UpToFourK(int w, int h)
        => Assert.Equal(64, TileBudget.SurfaceCapFor(w, h));

    [Fact]
    public void TheSurfaceCap_GrowsWithTheWindowArea_Beyond4K()
    {
        int at8k = TileBudget.SurfaceCapFor(7680, 4320);
        Assert.Equal(135, at8k);                                           // 90 tiles x 1.5 headroom
        Assert.True(at8k >= TileBudget.CoverTiles(7680, 4320));            // every visible tile fits
        Assert.Equal(64, TileBudget.SurfaceCapFor(5120, 2880));            // 5K at 100 %: 42 tiles x 3 MiB = 126 MiB, still under the fixed ceiling
        Assert.Equal(84, TileBudget.SurfaceCapFor(6144, 3456));            // 56 tiles x 3 MiB = 168 MiB -> 84 surfaces
        Assert.True(at8k > TileBudget.SurfaceCapFor(6144, 3456));          // monotone in the area
        Assert.Equal(200, TileBudget.SurfaceCapFor(7680, 4320, floorCap: 200));   // the floor wins when it is larger
    }

    [Fact]
    public void TheCeiling_NeverDropsBelowTheFixedValue_AndRisesForAnEightKWindow()
    {
        Assert.Equal(128 * Mib, TileBudget.CeilingFor(1920, 1080, TileBudget.DefaultCeilingBytes, TileBudget.DefaultCeilingCoverMultiplier));
        Assert.Equal(128 * Mib, TileBudget.CeilingFor(3840, 2160, TileBudget.DefaultCeilingBytes, TileBudget.DefaultCeilingCoverMultiplier));
        long eightK = TileBudget.CeilingFor(7680, 4320, TileBudget.DefaultCeilingBytes, TileBudget.DefaultCeilingCoverMultiplier);
        Assert.Equal(270 * Mib, eightK);                                   // 90 tiles x 2 MiB x 1.5
        Assert.True(eightK >= (long)TileBudget.CoverTiles(7680, 4320) * TileBudget.TileBytes);
        Assert.Equal(64 * Mib, TileBudget.CeilingFor(7680, 4320, 64 * Mib, 0.0));   // a zero multiplier turns the scaling off
    }

    [Fact]
    public void Current_UsesTheScaledCeiling_SoAnEightKWindowKeepsItsVisibleTilesResident()
    {
        long budget = TileBudget.Current(7680, 4320);
        Assert.True(budget > TileBudget.DefaultCeilingBytes);
        Assert.True(TileBudget.MaxResidentTiles(budget) >= TileBudget.CoverTiles(7680, 4320));
        // The unchanged cases: a normal window derives exactly what it always did.
        Assert.Equal(TileBudget.Compute(2560, 1600, TileBudget.DefaultWindowMultiplier, TileBudget.DefaultFloorBytes, TileBudget.DefaultCeilingBytes),
            TileBudget.Current(2560, 1600));
        Assert.Equal(TileBudget.DefaultCeilingBytes, TileBudget.Current(3840, 2160));   // 5 x window clamps at the fixed ceiling
        Assert.Equal(TileBudget.DefaultFloorBytes, TileBudget.Current(100, 100));
    }

    [Fact]
    public void ZeroingTheCoverMultiplier_RestoresTheFixedCeiling()
    {
        try
        {
            TileBudget.CeilingCoverMultiplier = 0.0;
            Assert.Equal(TileBudget.DefaultCeilingBytes, TileBudget.Current(7680, 4320));
        }
        finally { TileBudget.ResetToDefaults(); }
        Assert.True(TileBudget.Current(7680, 4320) > TileBudget.DefaultCeilingBytes);
    }

    [Fact]
    public void TheHostCap_UsesTheLargerOfTheClientAndTheMonitor()
    {
        Assert.Equal(64, AppHost.SliceSurfaceCapFor(new Size2(1280, 720), default));
        Assert.Equal(135, AppHost.SliceSurfaceCapFor(new Size2(7680, 4320), default));
        // A small window on a huge monitor is sized for the monitor: the user may maximize it later and the table is built once.
        Assert.Equal(135, AppHost.SliceSurfaceCapFor(new Size2(1280, 720), new SizeI(7680, 4320)));
    }

    [Fact]
    public void AnOrdinaryHeadlessHost_KeepsTheFloorCaps()
    {
        using var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var window = new HeadlessWindow(new WindowDesc("tile-caps", new Size2(1280, 720), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
            new EmptyRoot());
        Assert.Equal(64, host.UiSliceTable.SurfaceCap);
        Assert.Equal(64, host.UiSliceTable.TileCap);
    }
}
