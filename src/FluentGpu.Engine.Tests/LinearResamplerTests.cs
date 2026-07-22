using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Block-continuity gates for <see cref="LinearResampler"/> — <see cref="ResampleResult.Consumed"/> lets the caller
/// retain unread input when the production 44.1→48 pump fills <c>dst</c> before the source block is exhausted.
/// Assertions compare output PCM to an analytical / single-shot oracle only (no internal phase visibility).
/// </summary>
public sealed class LinearResamplerTests
{
    const int FromRate = 44100;
    const int ToRate = 48000;
    const int Ch = 2;
    // Production decode pump (pre-tighten over-pull): 884 src into 960 mix — maxOut binds before source exhaustion.
    const int ProdDstFrames = 960;
    const int ProdSrcFrames = 884;
    // Well over 32 blocks so a clamped ~64-frame residual/hold bug would lose samples and fail the oracle.
    const int StreamChunks = 48;

    [Fact]
    public void ProductionPump_44100To48000_LongStream_MatchesAnalyticalRamp()
    {
        int totalSrc = ProdSrcFrames * StreamChunks;
        float[] src = MakeRamp(totalSrc, Ch);

        float[] blocked = ResampleChunked(src, StreamChunks, ProdSrcFrames, dstFrames: ProdDstFrames, retainUnread: true);
        float[] expected = AnalyticalLinearUpsample(src, totalSrc);

        // Streaming leaves a short unread tail unflushed; compare the overlapping produced prefix.
        int n = Math.Min(blocked.Length, expected.Length);
        Assert.True(n >= ProdDstFrames * Ch * StreamChunks,
            $"expected a full {StreamChunks}-block stream, got {n / Ch} frames");

        double maxAbs = MaxAbsErr(expected.AsSpan(0, n), blocked.AsSpan(0, n));
        Assert.True(maxAbs < 1e-5, $"blocked vs analytical maxAbsErr={maxAbs} (nFloats={n}, chunks={StreamChunks})");
    }

    [Fact]
    public void ProductionPump_44100To48000_LongStream_MatchesSingleShotOracle()
    {
        int totalSrc = ProdSrcFrames * StreamChunks;
        float[] src = MakeRamp(totalSrc, Ch);

        float[] blocked = ResampleChunked(src, StreamChunks, ProdSrcFrames, dstFrames: ProdDstFrames, retainUnread: true);
        float[] oracle = ResampleSingleShot(src, totalSrc);

        int n = Math.Min(blocked.Length, oracle.Length);
        Assert.True(n >= ProdDstFrames * Ch * StreamChunks);
        double maxAbs = MaxAbsErr(oracle.AsSpan(0, n), blocked.AsSpan(0, n));
        Assert.True(maxAbs < 1e-5, $"blocked vs single-shot maxAbsErr={maxAbs} (nFloats={n})");
    }

    [Fact]
    public void DiscardingUnread_LongStream_DivergesFromAnalytical()
    {
        // Documents the bug: ignoring Consumed (dropping unread) diverges — and does so clearly over many blocks.
        int totalSrc = ProdSrcFrames * StreamChunks;
        float[] src = MakeRamp(totalSrc, Ch);
        float[] expected = AnalyticalLinearUpsample(src, totalSrc);
        float[] broken = ResampleChunked(src, StreamChunks, ProdSrcFrames, dstFrames: ProdDstFrames, retainUnread: false);

        int n = Math.Min(broken.Length, expected.Length);
        double maxAbs = MaxAbsErr(expected.AsSpan(0, n), broken.AsSpan(0, n));
        Assert.True(maxAbs > 1e-3, $"expected discard-path divergence over {StreamChunks} blocks, maxAbsErr={maxAbs}");
    }

    [Fact]
    public void SourceExhaustion_CrossBlock_MatchesAnalytical()
    {
        // Small src / large dst → exit via source exhaustion; stitch two blocks and check against analytical.
        float[] a = MakeRamp(32, Ch);
        float[] b = MakeRamp(32, Ch, start: 32);
        float[] all = new float[64 * Ch];
        a.CopyTo(all, 0);
        b.CopyTo(all, 32 * Ch);

        var rs = new LinearResampler(FromRate, ToRate, Ch);
        float[] dst = new float[4096 * Ch];
        var acc = new float[rs.MaxOutFrames(64) * Ch + 16];
        int written = 0;

        ResampleResult ra = rs.Process(a, 32, dst);
        dst.AsSpan(0, ra.Produced * Ch).CopyTo(acc.AsSpan(written * Ch));
        written += ra.Produced;
        Assert.Equal(32, ra.Consumed);

        ResampleResult rb = rs.Process(b, 32, dst);
        dst.AsSpan(0, rb.Produced * Ch).CopyTo(acc.AsSpan(written * Ch));
        written += rb.Produced;

        float[] expected = AnalyticalLinearUpsample(all, 64);
        int n = Math.Min(written * Ch, expected.Length);
        double maxAbs = MaxAbsErr(expected.AsSpan(0, n), acc.AsSpan(0, n));
        Assert.True(maxAbs < 1e-5, $"cross-block vs analytical maxAbsErr={maxAbs}");
    }

    [Fact]
    public void Passthrough_CopiesExact_ConsumedEqualsProduced()
    {
        var rs = new LinearResampler(48000, 48000, Ch);
        Assert.False(rs.IsActive);
        float[] src = MakeRamp(64, Ch);
        float[] dst = new float[64 * Ch];
        ResampleResult rr = rs.Process(src, 64, dst);
        Assert.Equal(64, rr.Produced);
        Assert.Equal(64, rr.Consumed);
        Assert.Equal(src, dst);
    }

    [Fact]
    public void Reset_MatchesFreshInstance_OnOutput()
    {
        var rs = new LinearResampler(FromRate, ToRate, Ch);
        float[] src = MakeRamp(ProdSrcFrames, Ch);
        float[] dst = new float[ProdDstFrames * Ch];
        rs.Process(src, ProdSrcFrames, dst);
        rs.Reset();

        var fresh = new LinearResampler(FromRate, ToRate, Ch);
        float[] a = new float[ProdDstFrames * Ch];
        float[] b = new float[ProdDstFrames * Ch];
        ResampleResult ra = rs.Process(src, ProdSrcFrames, a);
        ResampleResult rb = fresh.Process(src, ProdSrcFrames, b);
        Assert.Equal(ra, rb);
        Assert.Equal(a, b);
    }

    [Fact]
    public void SrcFramesForOutput_MaxOutFitsWant()
    {
        var rs = new LinearResampler(FromRate, ToRate, Ch);
        int n = rs.SrcFramesForOutput(ProdDstFrames);
        Assert.True(n >= 2);
        Assert.True(rs.MaxOutFrames(n) <= ProdDstFrames,
            $"SrcFramesForOutput({ProdDstFrames})={n} → MaxOutFrames={rs.MaxOutFrames(n)}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static float[] MakeRamp(int frames, int ch, int start = 0)
    {
        var s = new float[frames * ch];
        for (int f = 0; f < frames; f++)
        {
            float v = (start + f) * 0.001f;
            for (int c = 0; c < ch; c++) s[f * ch + c] = v + c * 0.0001f;
        }
        return s;
    }

    /// <summary>
    /// Closed-form linear upsample of <see cref="MakeRamp"/>: output frame k reads source position
    /// <c>k * from/to</c> with the same lerp as <see cref="LinearResampler"/> (no negative-history bootstrap —
    /// matches a stream that starts at phase 0).
    /// </summary>
    static float[] AnalyticalLinearUpsample(float[] src, int totalSrcFrames)
    {
        double step = (double)FromRate / ToRate;
        // Last output needs src[i1] with i1 <= totalSrc-1 ⇒ floor(p)+1 <= totalSrc-1 ⇒ p < totalSrc-1
        int maxOut = 0;
        for (double p = 0; ; p += step, maxOut++)
        {
            int i1 = (int)Math.Floor(p) + 1;
            if (i1 > totalSrcFrames - 1) break;
        }

        var dst = new float[maxOut * Ch];
        double pos = 0;
        for (int o = 0; o < maxOut; o++)
        {
            int i0 = (int)Math.Floor(pos);
            int i1 = i0 + 1;
            float frac = (float)(pos - i0);
            for (int c = 0; c < Ch; c++)
            {
                float s0 = src[i0 * Ch + c];
                float s1 = src[i1 * Ch + c];
                dst[o * Ch + c] = s0 + (s1 - s0) * frac;
            }
            pos += step;
        }
        return dst;
    }

    static float[] ResampleSingleShot(float[] src, int totalSrcFrames)
    {
        var rs = new LinearResampler(FromRate, ToRate, Ch);
        var dst = new float[rs.MaxOutFrames(totalSrcFrames) * Ch + 16];
        ResampleResult rr = rs.Process(src.AsSpan(0, totalSrcFrames * Ch), totalSrcFrames, dst);
        return dst.AsSpan(0, rr.Produced * Ch).ToArray();
    }

    /// <param name="retainUnread">
    /// When true, slide <see cref="ResampleResult.Consumed"/> unread frames to the front before appending the next chunk
    /// (correct caller). When false, drop unread — the pre-fix bug.
    /// </param>
    static float[] ResampleChunked(float[] src, int chunks, int srcChunk, int dstFrames, bool retainUnread)
    {
        var rs = new LinearResampler(FromRate, ToRate, Ch);
        var dstScratch = new float[dstFrames * Ch];
        // Hold must absorb unread tail + next pull; grow if a buggy consumer retains too much (exposes 64-frame caps).
        var hold = new float[Math.Max(srcChunk * 4, 256) * Ch];
        int holdFrames = 0;
        int capacity = chunks * Math.Max(dstFrames, rs.MaxOutFrames(srcChunk)) * Ch + 64;
        var acc = new float[capacity];
        int written = 0;

        for (int c = 0; c < chunks; c++)
        {
            int needHold = (holdFrames + srcChunk) * Ch;
            if (needHold > hold.Length)
            {
                var bigger = new float[needHold + srcChunk * Ch];
                hold.AsSpan(0, holdFrames * Ch).CopyTo(bigger);
                hold = bigger;
            }
            src.AsSpan(c * srcChunk * Ch, srcChunk * Ch).CopyTo(hold.AsSpan(holdFrames * Ch));
            holdFrames += srcChunk;

            ResampleResult rr = rs.Process(hold.AsSpan(0, holdFrames * Ch), holdFrames, dstScratch);
            int need = (written + rr.Produced) * Ch;
            if (need > acc.Length)
            {
                var bigger = new float[need + dstFrames * Ch];
                acc.AsSpan(0, written * Ch).CopyTo(bigger);
                acc = bigger;
            }
            dstScratch.AsSpan(0, rr.Produced * Ch).CopyTo(acc.AsSpan(written * Ch));
            written += rr.Produced;

            if (retainUnread)
            {
                int unread = holdFrames - rr.Consumed;
                if (unread > 0 && rr.Consumed > 0)
                    Array.Copy(hold, rr.Consumed * Ch, hold, 0, unread * Ch);
                holdFrames = Math.Max(0, unread);
            }
            else
            {
                // Bug path: drop unread input (pre-fix Process always advanced past the whole block).
                holdFrames = 0;
            }
        }
        return acc.AsSpan(0, written * Ch).ToArray();
    }

    static double MaxAbsErr(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double max = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            double d = Math.Abs(a[i] - b[i]);
            if (d > max) max = d;
        }
        return max;
    }
}
