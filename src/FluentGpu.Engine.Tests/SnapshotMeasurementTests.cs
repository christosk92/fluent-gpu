using System;
using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class SnapshotMeasurementTests
{
    static (SceneStore Scene, NodeHandle Leaf) SceneWithBoxes(int count)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        scene.Root = scene.CreateNode(1);
        NodeHandle leaf = default;
        for (int i = 0; i < count; i++)
        {
            leaf = scene.CreateNode(2);
            scene.AppendChild(scene.Root, leaf);
        }
        return (scene, leaf);
    }

    static void Measure(SceneStore scene, NodeHandle node, float width, float fit)
    {
        scene.Paint(node).VisualKind = VisualKind.Text;
        scene.MeasureCacheRef(node).Store(new TextMeasureEntry
        {
            Valid = true, MaxW = width, FitSize = fit,
            Size = new Size2(width, fit + 3), UnderlineY = fit + 1,
            UnderlineThickness = 2, StrikeY = fit / 2,
        });
    }

    [Fact]
    public void MeasurementCapacityDoesNotFollowNonTextHighWater()
    {
        var (small, smallText) = SceneWithBoxes(32);
        var (large, largeText) = SceneWithBoxes(8000);
        Measure(small, smallText, 100, 12);
        Measure(large, largeText, 100, 12);
        var a = new SceneRecordingSnapshot();
        var b = new SceneRecordingSnapshot();
        a.Capture(small);
        b.Capture(large);
        Assert.Equal(1, a.MeasurementRowCount);
        Assert.Equal(1, b.MeasurementRowCount);
        Assert.Equal(a.MeasurementCapacity, b.MeasurementCapacity); // includes dictionary and free-list reserves
        Assert.Equal(16, b.MeasurementCapacity.Values);
        long reservedValueBytes = b.MeasurementCapacity.Values * Unsafe.SizeOf<TextMeasureCache>();
        long formerDenseValueBytes = b.Capacity * Unsafe.SizeOf<TextMeasureCache>();
        Assert.True(reservedValueBytes * 100 < formerDenseValueBytes);
    }

    [Fact]
    public void TwoWidthsAndMostRecentlyUsedFallbackRemainExact()
    {
        var (scene, node) = SceneWithBoxes(1);
        Measure(scene, node, 100, 12);
        Measure(scene, node, 80, 10);
        // A hit, not just a store, determines the fallback width.
        Assert.True(scene.MeasureCacheRef(node).TryGet(default, default, 100, out _));
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        Assert.Equal(12, snapshot.ResolveMeasureForWidth(node, 100).FitSize);
        Assert.Equal(10, snapshot.ResolveMeasureForWidth(node, 80).FitSize);
        var fallback = snapshot.ResolveMeasureForWidth(node, 75);
        Assert.Equal(12, fallback.FitSize);
        Assert.Equal(13, fallback.UnderlineY);
        Assert.Equal(2, fallback.UnderlineThickness);
        Assert.Equal(6, fallback.StrikeY);
    }

    [Fact]
    public void PublishedMeasurementDoesNotAliasLaterUiLayout()
    {
        var (scene, node) = SceneWithBoxes(1);
        Measure(scene, node, 100, 12);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        Measure(scene, node, 100, 24);
        var local = snapshot.ResolveMeasureForWidth(node, 100);
        local.FitSize = 99;
        Assert.Equal(12, snapshot.ResolveMeasureForWidth(node, 100).FitSize);
        Assert.Equal(24, scene.MeasureCacheRef(node).E1.FitSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParkUnparkDropsAndRestoresOnlyReachableMeasurements(bool incremental)
    {
        var (scene, node) = SceneWithBoxes(2);
        Measure(scene, node, 100, 12);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        scene.NotePublished(1);
        scene.Detach(node);
        if (incremental) Assert.True(snapshot.CaptureIncremental(scene, [], 1));
        else snapshot.Capture(scene);
        Assert.Equal(0, snapshot.MeasurementRowCount);
        Assert.False(snapshot.ResolveMeasureForWidth(node, 100).Valid);
        scene.NotePublished(2);
        scene.AppendChild(scene.Root, node);
        if (incremental) Assert.True(snapshot.CaptureIncremental(scene, [], 2));
        else snapshot.Capture(scene);
        Assert.Equal(1, snapshot.MeasurementRowCount);
        Assert.Equal(12, snapshot.ResolveMeasureForWidth(node, 100).FitSize);
        Assert.Equal(0, snapshot.IncrementalParityFailures);
    }

    [Fact]
    public void RecycledTextSlotCannotLeaveMeasurementsOnANonTextNode()
    {
        var (scene, old) = SceneWithBoxes(1);
        Measure(scene, old, 100, 12);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        scene.NotePublished(1);
        scene.FreeSubtree(old);
        var replacement = scene.CreateNode(2);
        scene.AppendChild(scene.Root, replacement);
        // This fixture bypasses the reconciler commit's bulk notification. The parent's captured FirstChild
        // contains a generation, so recycling its only child changes that row even when the index is unchanged.
        scene.NoteCaptureChanged((int)scene.Root.Raw.Index);
        Assert.Equal(old.Raw.Index, replacement.Raw.Index);
        Assert.NotEqual(old, replacement);
        Assert.True(snapshot.CaptureIncremental(scene, [], 1));
        Assert.Equal(0, snapshot.MeasurementRowCount);
        Assert.False(snapshot.ResolveMeasureForWidth(old, 100).Valid);
        Assert.False(snapshot.ResolveMeasureForWidth(replacement, 100).Valid);
        scene.NotePublished(2);
        Measure(scene, replacement, 100, 18);
        Assert.True(snapshot.CaptureIncremental(scene, [], 2));
        Assert.False(snapshot.ResolveMeasureForWidth(old, 100).Valid);
        Assert.Equal(18, snapshot.ResolveMeasureForWidth(replacement, 100).FitSize);
        Assert.Equal(0, snapshot.IncrementalParityFailures);
    }

    [Fact]
    public void FirstMeasuredRowAfterWarmedNonTextSceneNeedsNoCaptureAllocation()
    {
        var (scene, node) = SceneWithBoxes(100);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        snapshot.Capture(scene); // both retained reachable-list buffers are warm
        Measure(scene, node, 100, 12); // layout owns any first UI cache allocation, before capture
        long before = GC.GetAllocatedBytesForCurrentThread();
        snapshot.Capture(scene);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(1, snapshot.MeasurementRowCount);
    }

    [Fact]
    public void FirstIncrementalMeasurementAndItsFirstReplacementUseReservedSlots()
    {
        var (scene, node) = SceneWithBoxes(100);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        const ulong warmFrames = 4;
        for (ulong seq = 1; seq <= warmFrames; seq++)
        {
            scene.NotePublished(seq);
            Assert.True(snapshot.CaptureIncremental(scene, [], seq));
        }
        var capacity = snapshot.MeasurementCapacity;
        // Debug runs a reflection/boxing-based full parity auditor whose allocations depend on the captured data.
        // Do not subtract an unrelated baseline. Release capture must allocate exactly zero; Debug verifies
        // behavior and stable capacities. The separate column test below proves reserved storage in both builds.
        scene.NotePublished(warmFrames + 1);
        Measure(scene, node, 100, 12);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool captured = snapshot.CaptureIncremental(scene, [], warmFrames + 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(captured);
        Assert.True(SceneRecordingSnapshot.ParityVerifyCompiledIn || allocated == 0,
            $"Release incremental insertion allocated {allocated} bytes");
        Assert.Equal(capacity, snapshot.MeasurementCapacity);
        Assert.Equal(1, snapshot.MeasurementRowCount);
        Assert.Equal(12, snapshot.ResolveMeasureForWidth(node, 100).FitSize);
        scene.NotePublished(warmFrames + 2);
        Measure(scene, node, 80, 10);
        before = GC.GetAllocatedBytesForCurrentThread();
        captured = snapshot.CaptureIncremental(scene, [], warmFrames + 2);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(captured);
        Assert.True(SceneRecordingSnapshot.ParityVerifyCompiledIn || allocated == 0,
            $"Release incremental replacement allocated {allocated} bytes");
        Assert.Equal(capacity, snapshot.MeasurementCapacity);
        Assert.Equal(1, snapshot.MeasurementRowCount);
        Assert.Equal(10, snapshot.ResolveMeasureForWidth(node, 80).FitSize);
        Assert.Equal(0, snapshot.IncrementalParityFailures);
    }

    [Fact]
    public void ReservedColumnFirstInsertionRemovalAndReplacementAllocateNothing()
    {
        var entry = new TextMeasureCache();
        entry.Store(new TextMeasureEntry { Valid = true, MaxW = 100, FitSize = 12 });
        // Warm the generic code on a DIFFERENT column; target storage has never contained a row.
        var warm = new SnapshotColumn<TextMeasureCache>(reserveRemovals: true);
        warm.Set(100) = entry;
        warm.Remove(100);
        warm.Set(101) = entry;
        var target = new SnapshotColumn<TextMeasureCache>(reserveRemovals: true);
        var capacity = (target.ValueCapacity, target.IndexCapacity, target.FreeCapacity);
        long before = GC.GetAllocatedBytesForCurrentThread();
        target.Set(8000) = entry;
        target.Remove(8000);
        target.Set(9000) = entry;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(capacity, (target.ValueCapacity, target.IndexCapacity, target.FreeCapacity));
        Assert.Equal(1, target.RowCount);
        Assert.False(target.TryGet(8000, out _));
        Assert.True(target.TryGet(9000, out var current));
        Assert.Equal(12, current.ResolveForWidth(100).FitSize);
    }

    [Fact]
    public void SteadyFullCaptureAndRecorderLookupAllocateNothing()
    {
        var (scene, node) = SceneWithBoxes(100);
        Measure(scene, node, 100, 12);
        var snapshot = new SceneRecordingSnapshot();
        for (int i = 0; i < 4; i++)
        {
            snapshot.Capture(scene);
            _ = snapshot.ResolveMeasureForWidth(node, 100);
        }
        float total = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            snapshot.Capture(scene);
            total += snapshot.ResolveMeasureForWidth(node, 100).FitSize;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(1200, total);
    }
}
