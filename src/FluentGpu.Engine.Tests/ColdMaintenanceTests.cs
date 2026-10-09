using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

[Collection(SerialTestCollection.Name)]
public sealed class ColdMaintenanceTests
{
    [Fact]
    public void DeadlineCoalescesAndConsumesOnceWithoutChangingFasterPacing()
    {
        var policy = new ColdMaintenanceDeadline();
        Assert.Equal(-1, policy.ClampWait(-1, 0));
        Assert.True(policy.Arm(100));
        Assert.False(policy.Arm(20_000)); // An observation does not postpone the existing deadline.
        Assert.Equal(30_000, policy.ClampWait(-1, 100));
        Assert.Equal(0, policy.ClampWait(0, 100));
        Assert.Equal(16, policy.ClampWait(16, 100));
        Assert.False(policy.TryConsume(30_099));
        Assert.Equal(1, policy.ClampWait(-1, 30_100));
        Assert.True(policy.TryConsume(30_100));
        Assert.False(policy.TryConsume(90_000));
        Assert.Equal(-1, policy.ClampWait(-1, 90_000));
        Assert.True(policy.Arm(90_000));
    }

    [Fact]
    public void ConcurrentArmsHaveExactlyOneWakeWinner()
    {
        var policy = new ColdMaintenanceDeadline();
        int wakes = 0;
        Parallel.For(0, 64, _ => { if (policy.Arm(0)) Interlocked.Increment(ref wakes); });
        Assert.Equal(1, wakes);
        Assert.True(policy.TryConsume(30_000));
        Parallel.For(0, 64, _ => { if (policy.Arm(30_000)) Interlocked.Increment(ref wakes); });
        Assert.Equal(2, wakes);
        Assert.True(policy.TryConsume(60_000));
        Assert.Equal(-1, policy.ClampWait(-1, 60_000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PixelDeadlineRunsWithoutPaintAndReturnsToIndefiniteWait(bool minimized)
    {
        using var f = new Fixture(minimized);
        int submitted = f.Device.FrameCount;
        ulong published = f.Host.PresentedSequence;
        f.Host.PixelPool.Return(new byte[PixelBufferPool.MinBucketBytes]);
        f.Host.PixelPool.Return(new byte[PixelBufferPool.MinBucketBytes]);
        Assert.Equal(1, f.Host.ColdMaintenanceWakeCount);
        Assert.Equal(30_000, f.Host.RecommendedWaitMs());
        Assert.Equal(HostWaitKind.Idle, f.Host.LastWaitKind);
        Assert.False(f.Host.HasActiveWork);

        f.Now = 29_999;
        Assert.False(f.Host.RunFrame().Rendered);
        Assert.True(f.Host.PixelPool.RetainedBytes > 0);
        f.Now = 30_000;
        Assert.False(f.Host.RunFrame().Rendered);
        Assert.Equal(0, f.Host.PixelPool.RetainedBytes);
        Assert.Equal(1, f.Host.ColdMaintenanceRuns);
        Assert.Equal(-1, f.Host.RecommendedWaitMs());
        Assert.Equal(submitted, f.Device.FrameCount);
        Assert.Equal(published, f.Host.PresentedSequence);
        f.Now = 900_000;
        Assert.False(f.Host.RunFrame().Rendered);
        Assert.Equal(1, f.Host.ColdMaintenanceRuns);
        Assert.Equal(-1, f.Host.RecommendedWaitMs());
    }

    [Fact]
    public void AParkedLoopWakesForTheEarlierOfTheHiddenStageAndTheColdDeadlineThenSleeps()
    {
        using var f = new Fixture(true, hiddenStage: true);
        f.Host.PixelPool.Return(new byte[PixelBufferPool.MinBucketBytes]);
        f.Host.PixelPool.Return(new byte[PixelBufferPool.MinBucketBytes]);
        int first = f.Host.RecommendedWaitMs();
        Assert.InRange(first, 1, (int)HiddenMemoryBudget.DefaultShallowDelayMs);   // the hidden stage (2 s) is due before the 30 s pixel deadline

        f.Host.AdvanceFrameClockForTest(HiddenMemoryBudget.DefaultShallowDelayMs);
        Assert.False(f.Host.RunFrame().Rendered);
        Assert.Equal(HiddenStage.Shallow, f.Host.HiddenStageForTest);
        Assert.Equal(30_000, f.Host.RecommendedWaitMs());   // after the stage the wait returns to the cold deadline, not another stage wake

        f.Now = 30_000;
        Assert.False(f.Host.RunFrame().Rendered);
        Assert.Equal(1, f.Host.ColdMaintenanceRuns);
        Assert.Equal((int)(HiddenMemoryBudget.DefaultDeepDelayMs - HiddenMemoryBudget.DefaultShallowDelayMs), f.Host.RecommendedWaitMs());   // and once both ran, only the Deep stage's one wake is left (HiddenDeepHostTests)
    }

    [Fact]
    public void WorkerReturnAfterDrainRearmsWithoutPostingReactiveWork()
    {
        using var f = new Fixture(true);
        f.Host.PixelPool.Return(new byte[PixelBufferPool.MinBucketBytes]);
        f.Now = 30_000;
        f.Host.RunFrame();
        Task.Run(() => f.Host.PixelPool.Return(new byte[PixelBufferPool.MinBucketBytes])).GetAwaiter().GetResult();
        Assert.Equal(2, f.Host.ColdMaintenanceWakeCount);
        Assert.Equal(0, f.Host.PendingUiPostCount);
        Assert.False(f.Host.HasActiveWork);
        Assert.Equal(30_000, f.Host.RecommendedWaitMs());
        f.Now = 60_000;
        f.Host.RunFrame();
        Assert.Equal(0, f.Host.PixelPool.RetainedBytes);
        Assert.Equal(-1, f.Host.RecommendedWaitMs());
    }

    [Fact]
    public void RetentionCallbackRunsAfterParkingAndOutsideBucketLock()
    {
        var pool = new PixelBufferPool();
        int calls = 0;
        pool.BufferRetained += p =>
        {
            calls++;
            Assert.Equal(PixelBufferPool.MinBucketBytes, p.RetainedBytes);
            // A second thread can take the bucket lock before this callback returns.
            Task.Run(p.Trim).GetAwaiter().GetResult();
        };
        pool.Return(new byte[PixelBufferPool.MinBucketBytes]);
        Assert.Equal(1, calls);
        Assert.Equal(0, pool.RetainedBytes);
        pool.Return(new byte[3]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ReturnRacingTrimCannotLeaveRetainedBytesWithoutADeadline()
    {
        for (int i = 0; i < 64; i++)
        {
            var policy = new ColdMaintenanceDeadline();
            var pool = new PixelBufferPool();
            long now = 0;
            pool.BufferRetained += _ => policy.Arm(Interlocked.Read(ref now));
            pool.Return(new byte[PixelBufferPool.MinBucketBytes]);
            Interlocked.Exchange(ref now, 30_000);
            using var start = new ManualResetEventSlim();
            var returned = Task.Run(() => { start.Wait(); pool.Return(new byte[PixelBufferPool.MinBucketBytes]); });
            start.Set();
            Assert.True(policy.TryConsume(30_000));
            pool.Trim();
            returned.GetAwaiter().GetResult();
            if (pool.RetainedBytes > 0) Assert.InRange(policy.ClampWait(-1, 30_000), 1, 30_000);
        }
    }

    [Fact]
    public void PoolReplacementAndDisposalDetachWorkerNotifications()
    {
        var f = new Fixture(true);
        var old = f.Host.PixelPool;
        var replacement = new PixelBufferPool();
        replacement.Return(new byte[PixelBufferPool.MinBucketBytes]);
        f.Host.PixelPool = replacement; // Existing retention also arms, without waiting for another return.
        Assert.Equal(1, f.Host.ColdMaintenanceWakeCount);
        f.Now = 30_000;
        f.Host.RunFrame();
        old.Return(new byte[PixelBufferPool.MinBucketBytes]);
        Assert.Equal(1, f.Host.ColdMaintenanceWakeCount);
        Assert.Equal(-1, f.Host.RecommendedWaitMs());
        f.Dispose();
        Task.Run(() => replacement.Return(new byte[PixelBufferPool.MinBucketBytes])).GetAwaiter().GetResult();
        Assert.Equal(1, f.Host.ColdMaintenanceWakeCount);
    }

    [Fact]
    public void SceneHighWaterShrinksOnceAndFailedTrimDoesNotRearm()
    {
        using var f = new Fixture(true);
        var nodes = new NodeHandle[1024];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = f.Host.Scene.CreateNode(1);
        int high = f.Host.Scene.Capacity;
        Assert.Equal(30_000, f.Host.RecommendedWaitMs());
        f.Now = 30_000;
        f.Host.RunFrame(); // All high slots live: an intentionally unsuccessful attempt.
        Assert.Equal(high, f.Host.Scene.Capacity);
        Assert.Equal(-1, f.Host.RecommendedWaitMs());
        f.Now = 500_000;
        f.Host.RunFrame();
        Assert.Equal(1, f.Host.ColdMaintenanceRuns);

        foreach (var node in nodes) f.Host.Scene.FreeSubtree(node);
        Assert.Equal(30_000, f.Host.RecommendedWaitMs());
        f.Now = 530_000;
        int submitted = f.Device.FrameCount;
        Assert.False(f.Host.RunFrame().Rendered);
        Assert.Equal(256, f.Host.Scene.Capacity);
        Assert.Equal(submitted, f.Device.FrameCount);
        Assert.Equal(-1, f.Host.RecommendedWaitMs());
        Assert.Equal(2, f.Host.ColdMaintenanceRuns);
    }

    [Fact]
    public void AllocatingServiceWaitsForQuietButNeverPastTheDeferCap()
    {
        Assert.Equal(long.MaxValue, ColdMaintenanceDeadline.Deferred(long.MaxValue, 50_000));   // nothing armed stays unarmed
        Assert.Equal(30_000, ColdMaintenanceDeadline.Deferred(30_000, 29_000));                // quiet before the deadline
        Assert.Equal(31_500, ColdMaintenanceDeadline.Deferred(30_000, 31_500));                // an interaction holds it back
        Assert.Equal(30_000 + ColdMaintenanceDeadline.MaxDeferMs,
            ColdMaintenanceDeadline.Deferred(30_000, 30_000 + 10 * ColdMaintenanceDeadline.MaxDeferMs));   // ...never forever

        var policy = new ColdMaintenanceDeadline();
        Assert.True(policy.Arm(0));
        Assert.False(policy.TryConsume(30_000, quietFromMs: 31_000));
        Assert.Equal(16, policy.ClampWaitQuiet(16, 30_000, 31_000));    // a display-rate wait is untouched: no 1 ms poll
        Assert.Equal(1_000, policy.ClampWaitQuiet(-1, 30_000, 31_000)); // an idle loop wakes at the quiet point
        Assert.True(policy.TryConsume(31_000, quietFromMs: 31_000));
        Assert.Equal(-1, policy.ClampWaitQuiet(-1, 31_000, 31_000));
    }

    [Fact]
    public void SceneTrimDueDuringInteractionWaitsUntilTheHostIsQuiet()
    {
        // 2026-10-06 real-data bench: the 30 s scene deadline fell due two seconds into a Liked Songs scroll and the trim
        // (every SoA column reallocated, 6.65 MB of large-object arrays) ran inside a scroll frame.
        using var f = new Fixture(minimized: false);
        var nodes = new NodeHandle[1024];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = f.Host.Scene.CreateNode(1);
        foreach (var node in nodes) f.Host.Scene.FreeSubtree(node);
        int high = f.Host.Scene.Capacity;
        Assert.True(high > 256);
        Assert.Equal(30_000, f.Host.RecommendedWaitMs());

        f.Now = 30_000;
        f.Window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(10f, 10f), 0, 0));
        f.Host.RunFrame();                                   // due, but the person is interacting
        Assert.Equal(high, f.Host.Scene.Capacity);
        Assert.Equal(0, f.Host.ColdMaintenanceRuns);

        f.Now = 30_000 + ColdMaintenanceDeadline.QuietMs - 1;
        f.Host.RunFrame();
        Assert.Equal(high, f.Host.Scene.Capacity);
        Assert.Equal(1, f.Host.RecommendedWaitMs());         // the idle loop wakes for it at the quiet point, not before

        f.Now = 30_000 + ColdMaintenanceDeadline.QuietMs;
        f.Host.RunFrame();
        Assert.Equal(256, f.Host.Scene.Capacity);
        Assert.Equal(1, f.Host.ColdMaintenanceRuns);
        Assert.Equal(-1, f.Host.RecommendedWaitMs());
    }

    [Fact]
    public void SceneRevisionTracksEqualCountReplacementButNotPaint()
    {
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        ulong revision = scene.CapacityRevision;
        scene.Paint(node).Opacity = 0.5f;
        scene.Mark(node, NodeFlags.PaintDirty);
        Assert.Equal(revision, scene.CapacityRevision);
        scene.FreeSubtree(node);
        scene.CreateNode(1);
        Assert.Equal(1, scene.LiveCount);
        Assert.Equal(revision + 2, scene.CapacityRevision);
    }

    [Fact]
    public void SnapshotDeadlineIncludesCooldownAndDisarmsAfterFailedAttempt()
    {
        SceneCapacityReclaimPolicy policy = default;
        Assert.Equal(long.MaxValue, policy.NextDeadlineMs(8192));
        policy.Observe(8192, 20, 100);
        Assert.Equal(30_100, policy.NextDeadlineMs(8192));
        policy.NoteAttempt(30_100);
        Assert.Equal(long.MaxValue, policy.NextDeadlineMs(8192));
        policy.Observe(8192, 20, 30_101);
        Assert.Equal(150_100, policy.NextDeadlineMs(8192));
        policy.Observe(8192, 7000, 30_102);
        Assert.Equal(long.MaxValue, policy.NextDeadlineMs(8192));
    }

    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    private sealed class Fixture : IDisposable
    {
        internal long Now;
        internal readonly HeadlessPlatformApp App = new();
        internal readonly HeadlessWindow Window = new(new WindowDesc("cold-maintenance", new Size2(320, 240), 1));
        internal readonly HeadlessGpuDevice Device = new();
        internal readonly AppHost Host;
        internal Fixture(bool minimized, bool hiddenStage = false)
        {
            // These tests pin the cold-maintenance deadlines of a parked loop; the hidden-memory stage's own one-shot wake
            // (HiddenStageHostTests) would only add a second deadline to what they measure - except the one test that pins both.
            if (!hiddenStage) HiddenMemoryBudget.ShallowDelayMs = long.MaxValue;
            var strings = new StringTable();
            Window.Show();
            Host = new AppHost(App, Window, Device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
            Host.ColdMaintenanceClock = () => Interlocked.Read(ref Now);
            for (int i = 0; i < 8; i++) Host.RunFrame();
            if (minimized) { Window.State = WindowState.Minimized; Host.RunFrame(); }
            if (!hiddenStage) Assert.Equal(-1, Host.RecommendedWaitMs());
        }
        public void Dispose() { Host.Dispose(); App.Dispose(); HiddenMemoryBudget.Reset(); }
    }
}
