using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The spectrum tap's SPSC mono ring in the CONTENT-frame domain (<see cref="SpectrumRing"/>): downmix, the arm that maps
/// content frames onto a monotonic ring index, and every reason <c>TryCopyContentWindow</c> refuses a window (before the
/// arm, not yet written, inside the producer's next block, across a re-arm). Sample VALUES are the content frame number
/// throughout (exact in <c>float</c> below 2^24), so a returned window is checked sample by sample.
/// </summary>
public sealed class SpectrumRingTests
{
    /// <summary>Mono samples whose value is their content frame: <c>first, first+1, …</c>.</summary>
    private static float[] Ramp(long first, int count)
    {
        var x = new float[count];
        for (int i = 0; i < count; i++) x[i] = (float)(first + i);
        return x;
    }

    /// <summary>Write <paramref name="mono"/> as mono blocks of at most <paramref name="block"/> samples (the producer never writes
    /// more than <c>maxBlock</c> in one call, which is what the ring's guard band assumes).</summary>
    private static void WriteMono(SpectrumRing ring, float[] mono, int block)
    {
        for (int i = 0; i < mono.Length; i += block)
            ring.Write(mono.AsSpan(i, Math.Min(block, mono.Length - i)), 1);
    }

    private static void AssertRamp(long firstContent, float[] window)
    {
        for (int i = 0; i < window.Length; i++)
            if (window[i] != (float)(firstContent + i))
                Assert.Fail($"sample {i}: expected content frame {firstContent + i}, got {window[i]}");
    }

    // ── construction ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-5, 16)]
    [InlineData(1, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 32)]
    [InlineData(66150, 131072)]     // 1.5 s at 44.1 kHz
    [InlineData(72000, 131072)]     // 1.5 s at 48 kHz  (2.7 s of audio — the plan's ring)
    [InlineData(131072, 131072)]
    [InlineData(144000, 262144)]    // 1.5 s at 96 kHz
    public void Capacity_is_the_requested_size_rounded_up_to_a_power_of_two(int minSamples, int expected)
    {
        Assert.Equal(expected, new SpectrumRing(minSamples, 1024).Capacity);
    }

    [Fact]
    public void A_new_ring_is_unarmed_and_refuses_every_window()
    {
        var ring = new SpectrumRing(256, 16);
        Assert.Equal(0, ring.ArmEpoch);
        Assert.Equal(0, ring.Written);
        Assert.Equal(0, ring.NewestContent);
        WriteMono(ring, Ramp(0, 64), 16);
        Assert.Equal(64, ring.Written);
        Assert.Equal(0, ring.NewestContent);                          // no content domain yet
        Assert.False(ring.TryCopyContentWindow(64, new float[16]));   // written, but never armed
    }

    // ── write ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_downmixes_stereo_to_mono()
    {
        var ring = new SpectrumRing(256, 16);
        ring.Arm(0);
        float[] stereo = [1f, 3f,   2f, -2f,   0.5f, 0.25f,   10f, 20f];
        ring.Write(stereo, 2);
        Assert.Equal(4, ring.Written);
        var dst = new float[4];
        Assert.True(ring.TryCopyContentWindow(4, dst));
        Assert.Equal(new float[] { 2f, 0f, 0.375f, 15f }, dst);

        ring.Write(new float[] { 4f, 4f }, 2);                       // Written advances by frames and never resets
        Assert.Equal(5, ring.Written);
    }

    [Fact]
    public void Write_downmixes_mono_and_multichannel_and_ignores_partial_frames_and_bad_channel_counts()
    {
        var ring = new SpectrumRing(256, 16);
        ring.Arm(0);
        ring.Write(new float[] { 0.25f, 0.5f }, 1);                  // mono: copied through (2 frames)
        ring.Write(new float[] { 1f, 2f, 3f, 4f,   0f, 0f, 0f, 0f }, 4);   // quad: the mean of four (2 frames)
        ring.Write(new float[] { 8f, 8f, 8f }, 2);                   // 1 whole frame; the trailing sample is dropped
        Assert.Equal(5, ring.Written);

        long written = ring.Written;
        ring.Write(new float[] { 1f, 2f }, 0);                       // a nonsense channel count writes nothing
        ring.Write(new float[] { 1f, 2f }, -2);
        ring.Write(new float[] { 1f }, 2);                           // less than one frame
        ring.Write(ReadOnlySpan<float>.Empty, 2);
        Assert.Equal(written, ring.Written);

        var dst = new float[5];
        Assert.True(ring.TryCopyContentWindow(5, dst));
        Assert.Equal(new float[] { 0.25f, 0.5f, 2.5f, 0f, 8f }, dst);
    }

    // ── the arm: content frames ↔ ring index ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Arm_maps_content_frames_onto_the_ring()
    {
        var ring = new SpectrumRing(8192, 512);
        ring.Arm(1000);
        Assert.Equal(1, ring.ArmEpoch);
        WriteMono(ring, Ramp(1000, 4096), 512);
        Assert.Equal(1000 + 4096, ring.NewestContent);               // 5096

        var dst = new float[2048];
        Assert.True(ring.TryCopyContentWindow(1000 + 2048, dst));    // the FIRST 2048 samples written
        AssertRamp(1000, dst);
        Assert.True(ring.TryCopyContentWindow(1000 + 4096, dst));    // the newest 2048
        AssertRamp(1000 + 2048, dst);
    }

    [Fact]
    public void A_window_before_the_arm_is_rejected()
    {
        var ring = new SpectrumRing(256, 16);
        WriteMono(ring, Ramp(0, 100), 16);        // content that predates the arm (ring index 0..99)
        ring.Arm(500);                            // the next sample is content frame 500, ring index 100
        WriteMono(ring, Ramp(500, 100), 16);

        var dst = new float[64];
        Assert.False(ring.TryCopyContentWindow(540, dst));   // would start at ring index 76 < the arm at 100
        Assert.False(ring.TryCopyContentWindow(563, dst));   // starts at 99: one sample too early
        Assert.True(ring.TryCopyContentWindow(564, dst));    // starts exactly at the arm
        AssertRamp(500, dst);
        Assert.True(ring.TryCopyContentWindow(600, dst));
        AssertRamp(536, dst);
    }

    [Fact]
    public void A_window_inside_the_guard_band_is_rejected()
    {
        var ring = new SpectrumRing(64, 8);
        Assert.Equal(64, ring.Capacity);
        ring.Arm(0);
        WriteMono(ring, Ramp(0, 200), 8);         // written = 200: the producer's next block lands on ring index 200..207

        var dst = new float[16];
        Assert.False(ring.TryCopyContentWindow(150, dst));   // start 134 < 200 − 64 + 8 = 144: overwritten
        Assert.False(ring.TryCopyContentWindow(159, dst));   // start 143: still one sample inside the guard
        Assert.True(ring.TryCopyContentWindow(160, dst));    // start 144: the oldest sample the guard allows
        AssertRamp(144, dst);
        Assert.True(ring.TryCopyContentWindow(200, dst));
        AssertRamp(184, dst);
    }

    [Fact]
    public void A_window_reads_across_the_wrap()
    {
        var ring = new SpectrumRing(64, 8);
        ring.Arm(1000);
        WriteMono(ring, Ramp(1000, 130), 8);      // ring indices 0..129; index 128 and 129 wrapped to slots 0 and 1

        var dst = new float[16];
        Assert.True(ring.TryCopyContentWindow(1130, dst));   // ring indices 114..129: slots 50..63 then 0..1
        AssertRamp(1114, dst);
    }

    [Fact]
    public void A_rearm_invalidates_the_previous_domain()
    {
        var ring = new SpectrumRing(8192, 256);
        ring.Arm(0);
        WriteMono(ring, Ramp(0, 4096), 256);
        var dst = new float[512];
        Assert.True(ring.TryCopyContentWindow(2048, dst));
        AssertRamp(1536, dst);

        ring.Arm(90_000);                                    // a demand edge / device rebuild: no further writes yet
        Assert.Equal(2, ring.ArmEpoch);
        Assert.False(ring.TryCopyContentWindow(2048, dst));  // the same call: the old domain is gone
        Assert.Equal(90_000, ring.NewestContent);
        Assert.Equal(4096, ring.Written);                    // the write index never reset

        WriteMono(ring, Ramp(90_000, 1024), 256);
        Assert.Equal(90_000 + 1024, ring.NewestContent);
        Assert.False(ring.TryCopyContentWindow(90_000 + 100, dst));   // would straddle the re-arm
        Assert.True(ring.TryCopyContentWindow(90_000 + 512, dst));    // starts exactly at it
        AssertRamp(90_000, dst);
        Assert.True(ring.TryCopyContentWindow(90_000 + 1024, dst));
        AssertRamp(90_000 + 512, dst);
        Assert.False(ring.TryCopyContentWindow(2048, dst));           // the old domain stays unreachable
    }

    [Fact]
    public void A_window_past_the_newest_content_is_rejected()
    {
        var ring = new SpectrumRing(256, 16);
        ring.Arm(0);
        WriteMono(ring, Ramp(0, 1000), 16);
        Assert.Equal(1000, ring.NewestContent);

        var dst = new float[16];
        Assert.True(ring.TryCopyContentWindow(1000, dst));   // the newest window: the publisher saturates here
        AssertRamp(984, dst);
        Assert.False(ring.TryCopyContentWindow(ring.NewestContent + 1, dst));
        Assert.False(ring.TryCopyContentWindow(ring.NewestContent + 50, dst));
    }

    [Fact]
    public void A_window_must_fit_the_ring_less_one_block()
    {
        var ring = new SpectrumRing(64, 8);
        ring.Arm(0);
        WriteMono(ring, Ramp(0, 64), 8);
        Assert.False(ring.TryCopyContentWindow(64, Span<float>.Empty));
        Assert.False(ring.TryCopyContentWindow(64, new float[57]));   // > capacity − maxBlock
        Assert.False(ring.TryCopyContentWindow(64, new float[64]));
        Assert.True(ring.TryCopyContentWindow(64, new float[56]));    // capacity − maxBlock: the largest window
    }

    // ── concurrency and allocation ──────────────────────────────────────────────────────────────────────────────────

    private sealed class Shared
    {
        public long Accepted;
        public int Torn;
        public int Done;
    }

    [Fact]
    public async Task Concurrent_writer_reader_never_accepts_a_torn_or_cross_arm_window()
    {
        // The producer writes stereo blocks whose sample VALUE is the content frame and re-arms every 64 blocks into a
        // different content domain; the reader asks for windows ending a little behind NewestContent. Whatever the
        // interleaving, an ACCEPTED window must be the exact run of content frames it asked for — a torn read (the producer
        // overwrote it mid-copy) or a window straddling a re-arm would show as a value out of sequence.
        const int blockFrames = 256, window = 512, enough = 20_000;
        var ring = new SpectrumRing(1 << 16, blockFrames);
        var s = new Shared();

        var producer = Task.Run(() =>
        {
            var block = new float[blockFrames * 2];
            var clock = Stopwatch.StartNew();
            long arms = 0, next = 0;
            for (long i = 0; Volatile.Read(ref s.Accepted) < enough && clock.Elapsed < TimeSpan.FromSeconds(10); i++)
            {
                if (i % 64 == 0)
                {
                    next = (arms++ % 100) * 100_000L;   // 64 blocks = 16 384 samples < 100 000: domains never overlap; < 2^24 ⇒ exact floats
                    ring.Arm(next);
                }
                for (int f = 0; f < blockFrames; f++) { float v = (float)(next + f); block[2 * f] = v; block[2 * f + 1] = v; }
                ring.Write(block, 2);
                next += blockFrames;
                Thread.SpinWait(200);
            }
            Volatile.Write(ref s.Done, 1);
        });

        var consumer = Task.Run(() =>
        {
            var win = new float[window];
            while (Volatile.Read(ref s.Done) == 0)
            {
                long end = ring.NewestContent - 3 * blockFrames;
                if (end < window) continue;
                if (!ring.TryCopyContentWindow(end, win)) continue;
                for (int i = 0; i < window; i++)
                    if (win[i] != (float)(end - window + i)) { Interlocked.Increment(ref s.Torn); break; }
                Interlocked.Increment(ref s.Accepted);
            }
        });

        await Task.WhenAll(producer, consumer).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, s.Torn);
        Assert.True(s.Accepted >= enough, $"only {s.Accepted} windows were ever accepted — the reader never saw a stable window");
    }

    [Fact]
    public void Write_and_copy_steady_allocate_nothing()
    {
        var ring = new SpectrumRing(1 << 17, 1024);
        var block = new float[1024];   // 512 stereo frames
        for (int i = 0; i < block.Length; i++) block[i] = i * 0.001f;
        var dst = new float[2048];
        long content = 0;
        ring.Arm(content);
        for (int i = 0; i < 200; i++)   // warm
        {
            ring.Write(block, 2);
            content += 512;
            ring.TryCopyContentWindow(content, dst);
        }

        GC.Collect();
        int accepted = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++)
        {
            if (i % 100 == 0) ring.Arm(content);   // the RT arms on a demand edge / epoch change: that path is alloc-free too
            ring.Write(block, 2);
            content += 512;
            if (ring.TryCopyContentWindow(content, dst)) accepted++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(accepted > 0);
    }
}
