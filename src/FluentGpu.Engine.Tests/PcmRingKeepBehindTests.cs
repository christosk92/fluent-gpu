using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// D8 / seek design B — the ring's protected span behind the consumer head. The producer must never overwrite it,
/// <see cref="PcmRing.BehindFloats"/> must never claim more than is genuinely intact (V-PE11: after a flush, a clear, or a head
/// that has not yet been that far it is exact), and <see cref="PcmRing.TrySkipConsumerSide"/> /
/// <see cref="PcmRing.TryRewindConsumerSide"/> are bounded consumer-side moves. Every value written into these rings equals its
/// absolute float index, so a read that returns the wrong audio is caught by comparing against the index alone.
/// </summary>
public sealed class PcmRingKeepBehindTests
{
    /// <summary>Floats <paramref name="start"/>, <paramref name="start"/>+1, … — each value is its own absolute ring index.</summary>
    private static float[] Seq(long start, int count)
    {
        var values = new float[count];
        for (int i = 0; i < count; i++) values[i] = start + i;
        return values;
    }

    private static void AssertSeq(ReadOnlySpan<float> actual, long start)
    {
        for (int i = 0; i < actual.Length; i++) Assert.Equal((float)(start + i), actual[i]);
    }

    [Fact]
    public void KeepBehind_IsClampedToHalfTheCapacity_AndZeroIsTheClassicRing()
    {
        var clamped = new PcmRing(64, 1000);
        Assert.Equal(64, clamped.CapacityFloats);
        Assert.Equal(32, clamped.KeepBehindFloats);

        var classic = new PcmRing(64);
        Assert.Equal(0, classic.KeepBehindFloats);
        Assert.Equal(64, classic.FreeFloats);
        Assert.Equal(64, classic.Write(Seq(0, 64)));           // the whole capacity is usable
        Assert.Equal(0, classic.Write(Seq(64, 1)));
        Assert.Equal(64, classic.Read(new float[64]));
        Assert.Equal(0, classic.BehindFloats);                 // nothing is kept, so nothing is rewindable
        Assert.False(classic.TryRewindConsumerSide(1));
    }

    [Fact]
    public void Producer_NeverOverwritesTheKeptBehindSpan()
    {
        var ring = new PcmRing(64, 16);
        Assert.Equal(48, ring.FreeFloats);                     // capacity − unread − the protected span
        Assert.Equal(48, ring.Write(Seq(0, 64)));              // a short write: the behind span is reserved even before any read
        Assert.Equal(0, ring.Write(Seq(48, 1)));

        var first = new float[32];
        Assert.Equal(32, ring.Read(first));
        AssertSeq(first, 0);
        Assert.Equal(16, ring.BehindFloats);

        Assert.Equal(32, ring.Write(Seq(48, 32)));             // wraps over indices 0..15 — older than the kept span
        Assert.Equal(0, ring.Write(Seq(80, 1)));               // …but never over indices 16..31

        Assert.True(ring.TryRewindConsumerSide(16));
        var again = new float[16];
        Assert.Equal(16, ring.Read(again));
        AssertSeq(again, 16);                                  // the kept span survived the producer lapping the ring

        var rest = new float[48];
        Assert.Equal(48, ring.Read(rest));
        AssertSeq(rest, 32);                                   // and the wrap-around is seamless
    }

    [Fact]
    public void BehindFloats_NeverOverstates_AfterReadsRewindsAndFurtherReads()
    {
        var ring = new PcmRing(64, 16);
        Assert.Equal(0, ring.BehindFloats);                    // nothing has been played

        Assert.Equal(40, ring.Write(Seq(0, 40)));
        Assert.Equal(10, ring.Read(new float[10]));
        Assert.Equal(10, ring.BehindFloats);                   // only 10 floats exist behind the head
        Assert.Equal(20, ring.Read(new float[20]));
        Assert.Equal(16, ring.BehindFloats);                   // capped at the protected span

        Assert.True(ring.TryRewindConsumerSide(16));           // head 30 → 14
        Assert.Equal(0, ring.BehindFloats);                    // the furthest head was 30: nothing older than 14 is guaranteed
        Assert.False(ring.TryRewindConsumerSide(1));

        Assert.Equal(4, ring.Read(new float[4]));              // head 14 → 18, the furthest head is still 30
        Assert.Equal(4, ring.BehindFloats);                    // exact, not the 16 a naive "keepBehind" would claim
    }

    [Fact]
    public void SkipForward_CountsTowardTheFurthestHead_AndIsBoundedByThePublishedData()
    {
        var ring = new PcmRing(64, 16);
        Assert.Equal(40, ring.Write(Seq(0, 40)));

        Assert.False(ring.TrySkipConsumerSide(41));            // past the tail
        Assert.False(ring.TrySkipConsumerSide(-1));
        Assert.Equal(40, ring.AvailableFloats);                // a refused skip moves nothing

        Assert.True(ring.TrySkipConsumerSide(40));
        Assert.Equal(0, ring.AvailableFloats);
        Assert.Equal(16, ring.BehindFloats);                   // the skip advanced the furthest head: 16 intact, not 40
        Assert.Equal(0, ring.Read(new float[8]));
        Assert.True(ring.TrySkipConsumerSide(0));

        Assert.True(ring.TryRewindConsumerSide(16));
        var back = new float[16];
        Assert.Equal(16, ring.Read(back));
        AssertSeq(back, 24);                                   // the span the skip jumped over the end of
    }

    [Fact]
    public void Rewind_RefusesWhatIsNotIntact_AndMovesNothingWhenRefused()
    {
        var ring = new PcmRing(64, 16);
        Assert.Equal(40, ring.Write(Seq(0, 40)));
        Assert.Equal(12, ring.Read(new float[12]));

        Assert.False(ring.TryRewindConsumerSide(-1));
        Assert.False(ring.TryRewindConsumerSide(13));          // 13 > the 12 floats that exist behind the head
        Assert.Equal(28, ring.AvailableFloats);                // nothing moved
        Assert.True(ring.TryRewindConsumerSide(12));
        Assert.Equal(40, ring.AvailableFloats);
        Assert.Equal(0, ring.BehindFloats);
    }

    [Fact]
    public void Flush_MakesPreFlushAudioUnreachable_AndOnlyPostFlushAudioCountsAsKept()
    {
        var ring = new PcmRing(64, 16);
        Assert.Equal(40, ring.Write(Seq(0, 40)));
        Assert.Equal(20, ring.Read(new float[20]));
        Assert.Equal(16, ring.BehindFloats);                   // 20 played, but only the 16-float protected span is guaranteed

        ring.DiscardAllConsumerSide();                         // the seek flush: head jumps to the tail
        Assert.Equal(0, ring.AvailableFloats);
        Assert.Equal(0, ring.BehindFloats);                    // the old audio is pre-seek content
        Assert.False(ring.TryRewindConsumerSide(1));

        Assert.Equal(20, ring.Write(Seq(40, 20)));             // post-flush audio
        Assert.Equal(10, ring.Read(new float[10]));
        Assert.Equal(10, ring.BehindFloats);                   // 10, not the 16 the protected span could hold
        Assert.False(ring.TryRewindConsumerSide(11));
        Assert.True(ring.TryRewindConsumerSide(10));
        var again = new float[10];
        Assert.Equal(10, ring.Read(again));
        AssertSeq(again, 40);
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var ring = new PcmRing(64, 16);
        Assert.Equal(40, ring.Write(Seq(0, 40)));
        Assert.Equal(30, ring.Read(new float[30]));
        ring.Clear();
        Assert.Equal(0, ring.AvailableFloats);
        Assert.Equal(0, ring.BehindFloats);
        Assert.False(ring.TryRewindConsumerSide(1));
        Assert.Equal(48, ring.FreeFloats);
    }

    [Fact]
    public void RandomisedOperations_KeepEveryReadExact_AndNeverOverstateBehind()
    {
        var ring = new PcmRing(64, 16);
        var rng = new Random(20261002);
        long head = 0, tail = 0, floor = 0;                    // an independent model of the ring's three cursors
        var dst = new float[64];

        for (int step = 0; step < 50_000; step++)
        {
            switch (rng.Next(6))
            {
                case 0:
                case 1:
                {
                    int n = rng.Next(1, 48);
                    int written = ring.Write(Seq(tail, n));
                    Assert.InRange(written, 0, n);
                    tail += written;
                    break;
                }
                case 2:
                case 3:
                {
                    int n = rng.Next(1, 48);
                    int read = ring.Read(dst.AsSpan(0, n));
                    for (int i = 0; i < read; i++) Assert.Equal((float)(head + i), dst[i]);   // exact audio, even after rewinds
                    head += read;
                    break;
                }
                case 4:
                {
                    int n = rng.Next(0, 24);
                    bool ok = ring.TrySkipConsumerSide(n);
                    Assert.Equal(n <= tail - head, ok);
                    if (ok) head += n;
                    break;
                }
                default:
                {
                    if (rng.Next(8) == 0) { ring.DiscardAllConsumerSide(); head = tail; floor = tail; break; }
                    int n = rng.Next(0, 24);
                    int behind = ring.BehindFloats;
                    Assert.True(head - behind >= tail - 64, "BehindFloats claimed audio the producer has already overwritten");
                    Assert.True(head - behind >= floor, "BehindFloats claimed audio from before the last flush");
                    bool ok = ring.TryRewindConsumerSide(n);
                    Assert.Equal(n <= behind, ok);
                    if (ok) head -= n;
                    break;
                }
            }
            Assert.Equal((int)(tail - head), ring.AvailableFloats);
        }
    }

    [Fact]
    public void SpscThreads_StayExact_WhileTheConsumerKeepsRewinding()
    {
        const int total = 400_000;
        var ring = new PcmRing(1024, 256);
        var producer = new Thread(() =>
        {
            long next = 0;
            var chunk = new float[61];
            var clock = Stopwatch.StartNew();
            while (next < total && clock.ElapsedMilliseconds < 20_000)
            {
                int n = (int)Math.Min(chunk.Length, total - next);
                for (int i = 0; i < n; i++) chunk[i] = next + i;
                int written = ring.Write(chunk.AsSpan(0, n));
                if (written == 0) Thread.Yield();
                else next += written;
            }
        }) { IsBackground = true };

        string? failure = null;
        long head = 0;
        int reads = 0;
        var dst = new float[37];
        var consumerClock = Stopwatch.StartNew();
        producer.Start();
        while (head < total && failure is null && consumerClock.ElapsedMilliseconds < 20_000)
        {
            int read = ring.Read(dst);
            if (read == 0) { Thread.Yield(); continue; }
            for (int i = 0; i < read; i++)
            {
                if (dst[i] != head + i) { failure = $"float {head + i}: read {dst[i]}"; break; }
            }
            head += read;
            if (++reads % 40 == 0 && head < total)
            {
                int k = Math.Min(ring.BehindFloats, 64);
                if (k > 0 && ring.TryRewindConsumerSide(k)) head -= k;   // re-read audio the producer may be lapping right now
            }
        }
        Assert.True(producer.Join(5000), "the producer never finished");
        Assert.Null(failure);
        Assert.Equal(total, head);
    }

    [Fact]
    public void WriteReadSkipRewind_AllocateNothing()
    {
        var ring = new PcmRing(1024, 256);
        var source = new float[64];
        var dst = new float[64];

        void Cycle()
        {
            ring.Write(source);
            ring.Read(dst);
            ring.TryRewindConsumerSide(32);
            ring.Read(dst.AsSpan(0, 32));
            ring.TrySkipConsumerSide(0);
            _ = ring.BehindFloats;
        }

        for (int i = 0; i < 200; i++) Cycle();                 // warm the JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) Cycle();
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
