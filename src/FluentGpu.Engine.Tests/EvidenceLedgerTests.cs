using System;
using FluentGpu.Render.Evidence;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The evidence rings (docs/plans/evidence-diagnostics-implementation.md §A.1/§A.4): the fixed single-producer
/// ring every ledger shares drains in write order, keeps only the newest <c>Capacity</c> records once it wraps, resumes a
/// fallen-behind reader at the oldest retained record, and lets the producer fill a late field (a walk's byte count) only
/// while the record is still retained.</summary>
public sealed class RasterLedgerTests
{
    private static RasterEntry Entry(int frame) => new() { Frame = frame, SliceId = frame % 7, Hash = (ulong)frame * 31UL };

    [Fact]
    public void Ring_WrapsAndDrainsInOrder()
    {
        var ring = new DiagRing<RasterEntry>(8);
        for (int i = 1; i <= 5; i++) ring.Add(Entry(i));
        var dst = new RasterEntry[16];

        int n = ring.Read(0, dst, out long next);
        Assert.Equal(5, n);
        Assert.Equal(5, next);
        for (int i = 0; i < n; i++) Assert.Equal(i + 1, dst[i].Frame);

        for (int i = 6; i <= 13; i++) ring.Add(Entry(i));   // 13 written into 8 slots: 1..5 are gone
        n = ring.Read(0, dst, out next);
        // The oldest slot (record 6) is the one the NEXT write lands in, so the torn-read guard drops it: a reader never
        // returns a record the producer may be overwriting while it copies (ScrollProbe.ReadRender's rule).
        Assert.Equal(7, n);
        Assert.Equal(13, next);
        for (int i = 0; i < n; i++) Assert.Equal(7 + i, dst[i].Frame);   // the oldest SAFE record first

        n = ring.Read(next, dst, out long after);
        Assert.Equal(0, n);
        Assert.Equal(13, after);
    }

    [Fact]
    public void ReadTail_ReturnsTheNewestRecordsOldestFirst()
    {
        var ring = new RasterLedger();
        for (int i = 1; i <= 10; i++) ring.Add(Entry(i));
        var tail = new RasterEntry[3];
        int n = ring.ReadTail(tail);
        Assert.Equal(3, n);
        Assert.Equal(new[] { 8, 9, 10 }, new[] { tail[0].Frame, tail[1].Frame, tail[2].Frame });
    }

    [Fact]
    public void Capacity_MustBeAPowerOfTwo()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagRing<RasterEntry>(6));
        Assert.Equal(RasterLedger.RasterLedgerCapacity, new RasterLedger().Capacity);
        Assert.Equal(WalkLedger.WalkLedgerCapacity, new WalkLedger().Capacity);
    }

    [Fact]
    public void WalkLedger_SetBytes_FillsARetainedEntryAndIgnoresAnOverwrittenOne()
    {
        var walks = new WalkLedger();
        long first = walks.Add(new WalkEntry { Frame = 1, NodeIndex = 42, Bytes = -1, Why = (byte)WalkWhy.SigMiss });
        walks.SetBytes(first, 4096);
        var dst = new WalkEntry[1];
        walks.Read(first, dst, out _);
        Assert.Equal(4096, dst[0].Bytes);
        Assert.Equal((byte)WalkWhy.SigMiss, dst[0].Why);

        for (int i = 0; i < WalkLedger.WalkLedgerCapacity; i++) walks.Add(new WalkEntry { Frame = 2, Bytes = -1 });
        walks.SetBytes(first, 1);   // long overwritten: must not scribble over the slot's new occupant
        var all = new WalkEntry[WalkLedger.WalkLedgerCapacity];
        int n = walks.Read(0, all, out _);
        for (int i = 0; i < n; i++) Assert.Equal(-1, all[i].Bytes);
    }

    [Fact]
    public void WalkClassifier_FollowsTheKeepTestsOrder()
    {
        // the #1 mechanism: a clean subtree, a span stored for the current buffer, but under another signature
        Assert.Equal(WalkWhy.SigMiss, WalkClassifier.Classify(true, false, 0, false, 0, false, false, 2, true, out _));
        Assert.Equal(WalkWhy.NoPriorSpan, WalkClassifier.Classify(true, false, 0, false, 0, false, false, 1, true, out _));
        Assert.Equal(WalkWhy.PartialSpan, WalkClassifier.Classify(true, false, 0, false, 0, false, false, 0, false, out _));
        Assert.Equal(WalkWhy.RootTail, WalkClassifier.Classify(true, false, 0, false, 0, true, true, 2, true, out _));
        Assert.Equal(WalkWhy.KeepDeny, WalkClassifier.Classify(true, false, 0, false, 0, true, false, 2, true, out _));
        Assert.Equal(WalkWhy.RecordDirty, WalkClassifier.Classify(true, false, 0, false, 3, true, false, 2, true, out uint bits));
        Assert.Equal(3u, bits);
        Assert.Equal(WalkWhy.Blocked, WalkClassifier.Classify(true, false, 0, true, 3, true, false, 2, true, out _));
        Assert.Equal(WalkWhy.ReuseOff, WalkClassifier.Classify(true, true, 0x44, true, 3, true, false, 2, true, out uint why));
        Assert.Equal(0x44u, why);
        Assert.Equal(WalkWhy.SpansOff, WalkClassifier.Classify(false, true, 0x44, true, 3, true, false, 2, true, out _));
        Assert.Equal(WalkWhy.None, WalkClassifier.Classify(true, false, 0, false, 0, false, false, 0, true, out _));
    }

    [Fact]
    public void Add_AllocatesNothing()
    {
        var ring = new RasterLedger();
        var e = Entry(1);
        ring.Add(e);   // warm
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) ring.Add(e);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
