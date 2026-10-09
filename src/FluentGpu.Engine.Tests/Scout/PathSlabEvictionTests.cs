using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// PathRealizationCache eviction. The engine's record turn must compact the retained slab, and no recorded path byte
/// (a copied span, a kept slice) may outlive the slab offsets a compaction moved.
/// </summary>
[Collection(SerialTestCollection.Name)]   // lowers GpuProfile.PathSlabBudgetBytes; reads the process-global PathRealizationCache.Shared
public sealed class PathSlabEvictionTests
{
    static PathData NGon(int n, float r, float cx, float cy)
    {
        var b = new PathBuilder();
        for (int i = 0; i < n; i++)
        {
            float a = 2f * MathF.PI * i / n;
            float x = cx + r * MathF.Cos(a), y = cy + r * MathF.Sin(a);
            if (i == 0) b.MoveTo(x, y); else b.LineTo(x, y);
        }
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    [Fact]
    public void Compaction_bumps_the_generation_waits_for_the_slab_to_double_and_never_allocates_when_nothing_is_evictable()
    {
        int saved = GpuProfile.PathSlabBudgetBytes;
        const int budget = 16 * 1024;
        GpuProfile.PathSlabBudgetBytes = budget;
        try
        {
            var cache = new PathRealizationCache();
            cache.BeginFrame(1);
            for (int i = 0; cache.SlabBytes <= budget; i++) cache.TryRealizeFill(NGon(64, 20f + i, 50f, 50f), FillRule.NonZero, 1f, out _);

            // Over budget, but everything is inside the quarantine window: a scan, never a compaction or an allocation.
            cache.BeginFrame(2);
            long before = GC.GetAllocatedBytesForCurrentThread();
            cache.BeginFrame(3);
            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(0UL, cache.Generation);
            Assert.Equal(0, cache.EvictionCount);

            // A live set of about 60% of the budget, realized on frame 3; the frame-1 set goes stale.
            long liveStart = cache.SlabBytes;
            for (int i = 0; cache.SlabBytes - liveStart < budget * 6 / 10; i++) cache.TryRealizeFill(NGon(64, 30f + i, 50f, 50f), FillRule.NonZero, 1f, out _);
            cache.BeginFrame(5);   // protectFrom = 3: the frame-1 set goes, the frame-3 set stays
            Assert.Equal(1UL, cache.Generation);
            Assert.True(cache.EvictionCount > 0);
            long kept = cache.SlabBytes;
            Assert.True(kept > budget / 2 && kept < budget);

            // Past the budget but not past 2x what survived: no second compaction yet.
            while (cache.SlabBytes <= budget) cache.TryRealizeFill(NGon(64, 5f + cache.SlabBytes % 97, 50f, 50f), FillRule.NonZero, 1f, out _);
            Assert.True(cache.SlabBytes <= 2 * kept);
            cache.BeginFrame(20);
            Assert.Equal(1UL, cache.Generation);
            while (cache.SlabBytes <= 2 * kept) cache.TryRealizeFill(NGon(64, 5f + cache.SlabBytes % 97, 50f, 50f), FillRule.NonZero, 1f, out _);
            cache.BeginFrame(30);
            Assert.Equal(2UL, cache.Generation);
        }
        finally { GpuProfile.PathSlabBudgetBytes = saved; }
    }

    private sealed class Probe : Component
    {
        public readonly Signal<int> Tick = new(0);
        public readonly PathData Static = NGon(6, 20f, 25f, 25f);   // minted ONCE: its node stays clean, so its span is reused
        public override Element Render()
        {
            int t = Tick.Value;
            return new BoxEl
            {
                Children =
                [
                    // A fresh epoch every tick (CartesianChart mints one per resize/remount build). It sits FIRST in tree
                    // order, so its first tessellation lies below the static one and a compaction relocates the static one.
                    new PathEl { Width = 200f, Height = 200f, Geometry = NGon(400, 90f + t % 5, 100f, 100f), Fill = ColorF.FromRgba(255, 0, 0, 255) },
                    new PathEl { Width = 50f, Height = 50f, Geometry = Static, Fill = ColorF.FromRgba(0, 255, 0, 255) },
                ],
            };
        }
    }

    [Theory]
    [InlineData(false)]   // headless single-thread: SceneRecorder.Record on the UI thread
    [InlineData(true)]    // fgpu-render thread: SceneRenderFrame.Record + the composite-only gate
    public void Record_turns_compact_the_slab_and_never_replay_moved_offsets(bool renderThread)
    {
        int saved = GpuProfile.PathSlabBudgetBytes;
        GpuProfile.PathSlabBudgetBytes = 64 * 1024;
        try
        {
            var strings = new StringTable();
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("path-slab", new Size2(320, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var probe = new Probe();
            using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, probe);
            if (renderThread) host.InstallRenderThreadForTest();
            var cache = PathRealizationCache.Shared;
            int evictionsBefore = cache.EvictionCount;
            ulong genBefore = cache.Generation;

            for (int frame = 0; frame < 60; frame++)
            {
                probe.Tick.Value = frame;
                host.RunFrame();
                if (frame < 3) continue;
                FillPathCmd? drawn = null;
                foreach (var f in device.LastFillPaths) if (f.Fill.G > 0.5f && f.Fill.R < 0.5f) drawn = f;
                Assert.True(drawn.HasValue, $"frame {frame}: the static path drew no FillPath");
                // The realization the slab holds NOW for the static path: a copied span or kept slice must index exactly this.
                Assert.True(cache.TryRealizeFill(probe.Static, FillRule.NonZero, 1f, out var now));
                Assert.Equal((now.VtxStart, now.VtxCount, now.IdxStart, now.IdxCount),
                    (drawn.Value.VtxStart, drawn.Value.VtxCount, drawn.Value.IdxStart, drawn.Value.IdxCount));
            }

            Assert.True(cache.EvictionCount > evictionsBefore, "the engine's record turn never compacted the path slab");
            Assert.True(cache.Generation > genBefore);
            Assert.True(cache.SlabBytes < 1024 * 1024, $"slab={cache.SlabBytes} B after 60 fresh-epoch chart builds");
        }
        finally { GpuProfile.PathSlabBudgetBytes = saved; }
    }
}
