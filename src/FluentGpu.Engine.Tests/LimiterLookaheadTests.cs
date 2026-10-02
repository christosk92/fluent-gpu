using System;
using System.Collections.Immutable;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The terminal lookahead limiter (plan §4.7, E-3): no gain reduction below the ceiling, no sample above it, a linear attack
/// that finishes before the peak is heard, a release that snaps to unity, a delay that equals the reported latency on every
/// channel layout, block-size independence and in-place safety, zero managed allocation, and the graph wiring
/// (<c>Compile</c>/<c>BuildStage</c> pass the channel count, <c>LimiterSpec.LookaheadMs</c> reaches
/// <c>TotalLatencySamples</c>, a republish keeps the live limiter's delay line). Pure and deterministic — no device, no clock.
/// </summary>
public sealed class LimiterLookaheadTests
{
    private const int Rate = 48000;
    private const int Look = 96;   // the default 2 ms lookahead at 48 kHz
    private static readonly ParamPlane Plane = new();
    private static readonly float Ceiling = LimiterStage.DbToLinear(-1.5f);
    private static readonly float CeilingTol = Ceiling * 1.000001f;   // float rounding of gain × sample

    private static BlockCtx Ctx(int channels) => new(0, Rate, channels, Plane);

    /// <summary>Run <paramref name="src"/> through <paramref name="lim"/> into a fresh array, <paramref name="block"/> frames per call.</summary>
    private static float[] Run(LimiterStage lim, float[] src, int ch, int block = int.MaxValue)
    {
        int frames = src.Length / ch;
        var dst = new float[src.Length];
        for (int at = 0; at < frames; at += block)
        {
            int n = Math.Min(block, frames - at);
            lim.Process(src.AsSpan(at * ch, n * ch), dst.AsSpan(at * ch, n * ch), n, Ctx(ch));
        }
        return dst;
    }

    private static float[] Const(int frames, float v) { var a = new float[frames]; Array.Fill(a, v); return a; }

    /// <summary>Deterministic noise under a slow 0.1 → 1 envelope, so the gain keeps moving. Values in (−amplitude, amplitude).</summary>
    private static float[] Noise(int frames, int ch, float amplitude, uint seed)
    {
        var a = new float[frames * ch];
        uint s = seed;
        for (int f = 0; f < frames; f++)
        {
            float env = 0.1f + 0.9f * MathF.Abs(MathF.Sin(f * 0.0005f));
            for (int c = 0; c < ch; c++)
            {
                s = s * 1664525u + 1013904223u;
                a[f * ch + c] = amplitude * env * ((s >> 8) / 8388608f - 1f);
            }
        }
        return a;
    }

    /// <summary>Every output sample is at or under the ceiling (to float rounding), and the limiter limited rather than muted.</summary>
    private static void AssertHeldUnderTheCeiling(float[] src, int ch, int block = int.MaxValue)
    {
        var dst = Run(new LimiterStage(mixRate: Rate, channels: ch), src, ch, block);
        float max = 0f;
        int bad = -1;
        for (int i = 0; i < dst.Length; i++)
        {
            float a = MathF.Abs(dst[i]);
            if (a > CeilingTol && bad < 0) bad = i;
            max = MathF.Max(max, a);
        }
        Assert.True(bad < 0, $"sample {bad} = {(bad < 0 ? 0f : dst[bad])} exceeds the ceiling {Ceiling}");
        Assert.True(max >= 0.5f * Ceiling, $"the output peaks at {max}: the limiter over-attenuated (ceiling {Ceiling})");
    }

    // ── the delay equals the reported latency ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void LatencySamples_EqualsTheDelay_OnEveryChannelLayout(int ch)
    {
        var lim = new LimiterStage(mixRate: Rate, lookaheadMs: 2f, channels: ch);
        Assert.Equal(Look, lim.LatencySamples);

        const int frames = 400, at = 10;
        var src = new float[frames * ch];
        for (int c = 0; c < ch; c++) src[at * ch + c] = 0.1f * (c + 1);   // a distinct impulse per channel: a mix-up or a wrong delay shows
        var dst = Run(lim, src, ch);

        for (int f = 0; f < frames; f++)
            for (int c = 0; c < ch; c++)
                Assert.Equal(f == at + Look ? src[at * ch + c] : 0f, dst[f * ch + c]);
    }

    // ── nothing to do below the ceiling ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_50Hz_sine_at_0_8_gets_no_gain_reduction()
    {
        const int ch = 2, frames = 4800;
        var lim = new LimiterStage(mixRate: Rate, channels: ch);
        var src = new float[frames * ch];
        for (int f = 0; f < frames; f++)
        {
            src[f * ch] = 0.8f * (float)Math.Sin(2 * Math.PI * 50 * f / Rate);
            src[f * ch + 1] = 0.8f * (float)Math.Sin(2 * Math.PI * 50 * f / Rate + 1);
        }
        var dst = Run(lim, src, ch);

        for (int f = 0; f < frames; f++)
            for (int c = 0; c < ch; c++)
                Assert.Equal(f < Look ? 0f : src[(f - Look) * ch + c], dst[f * ch + c]);   // 0 dB: bit-exact, merely delayed
        Assert.Equal(1f, lim.CurrentGain);
    }

    // ── nothing above it ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_plus6dB_square_never_exceeds_the_ceiling()
    {
        const int ch = 2, frames = 9600;
        var src = new float[frames * ch];
        for (int f = 0; f < frames; f++)
        {
            float s = (f / 50) % 2 == 0 ? 2f : -2f;   // 480 Hz, ±2.0 (+6 dBFS)
            src[f * ch] = s;
            src[f * ch + 1] = -s;
        }
        AssertHeldUnderTheCeiling(src, ch);
    }

    [Fact]
    public void A_single_sample_spike_never_exceeds_the_ceiling()
    {
        const int ch = 2, frames = 9600;
        var src = new float[frames * ch];
        for (int f = 0; f < frames; f++)
        {
            float bg = 0.2f * (float)Math.Sin(2 * Math.PI * 440 * f / Rate);
            src[f * ch] = bg;
            src[f * ch + 1] = bg;
        }
        src[1000 * ch] = 4f;   // one frame, one channel, +12 dB
        AssertHeldUnderTheCeiling(src, ch);
    }

    [Fact]
    public void A_19kHz_inter_sample_peak_tone_never_exceeds_the_ceiling()
    {
        const int ch = 2, frames = 9600;
        var src = new float[frames * ch];
        for (int f = 0; f < frames; f++)
        {
            float s = (float)Math.Sin(2 * Math.PI * 19000 * f / Rate + 0.7);
            src[f * ch] = s;
            src[f * ch + 1] = s;
        }
        AssertHeldUnderTheCeiling(src, ch);
    }

    [Fact]
    public void Loud_noise_with_a_moving_envelope_never_exceeds_the_ceiling_at_any_block_size()
    {
        const int ch = 2;
        var src = Noise(20_000, ch, 3f, 777u);
        AssertHeldUnderTheCeiling(src, ch);        // one call
        AssertHeldUnderTheCeiling(src, ch, 61);    // ragged blocks: the window and the history span the block edges
    }

    // ── the attack is a ramp over the lookahead; the release is smooth and snaps ────────────────────────────────────

    [Fact]
    public void The_attack_is_a_linear_ramp_over_the_lookahead()
    {
        const int step = 2000, frames = step + 400;
        var lim = new LimiterStage(mixRate: Rate, channels: 1);
        var src = new float[frames];
        for (int f = 0; f < frames; f++) src[f] = f < step ? 0.5f : 2f;   // a sustained 0.5, then a +6 dB step at frame `step`
        var dst = Run(lim, src, 1);

        // Until the loud frame is scored (call step + 1) the gain is exactly unity: the delayed 0.5 comes out untouched.
        for (int k = Look; k <= step; k++) Assert.Equal(0.5f, dst[k]);

        // g[m] = the gain at call step + m. The delayed frame is still the 0.5 up to m = Look − 1, so the output shows the gain directly.
        var g = new float[Look];
        for (int m = 1; m < Look; m++) g[m] = dst[step + m] / 0.5f;

        Assert.True(g[1] < 1f, "the attack must start the moment the loud frame is scored");
        for (int m = 2; m < Look; m++) Assert.True(g[m] < g[m - 1], $"the gain stopped falling at call +{m}");
        // Linear: from the second call the window maximum is constant, so every step is the same size.
        for (int m = 4; m < Look; m++)
            Assert.InRange(MathF.Abs((g[m - 1] - g[m]) - (g[m - 2] - g[m - 1])), 0f, 2e-6f);

        // The ramp has all but arrived one call before the loud frame emerges, and that frame leaves at the ceiling.
        Assert.InRange(g[Look - 1], 0.38f, 0.43f);
        Assert.InRange(dst[step + Look], 0.9f * Ceiling, CeilingTol);
    }

    [Fact]
    public void The_release_is_smooth_and_snaps_to_unity()
    {
        const int burst = 480, tail = 60_000;
        var lim = new LimiterStage(releaseMs: 50f, mixRate: Rate, channels: 1);
        var src = new float[burst + tail];
        for (int f = 0; f < src.Length; f++) src[f] = f < burst ? 2f : 0.5f;
        var dst = Run(lim, src, 1);

        // The burst's last frame leaves the window around call burst + Look; the release starts there. One time constant
        // (50 ms = 2400 frames) later the gain has covered about 63 % of the way back — neither instant nor stuck.
        int oneTau = burst + Look + 2400;
        Assert.InRange(dst[oneTau] / 0.5f, 0.55f, 0.9f);

        // Long after, the gain has SNAPPED to exactly unity: the delayed input comes out bit-exact.
        Assert.Equal(1f, lim.CurrentGain);
        for (int k = src.Length - 200; k < src.Length; k++) Assert.Equal(0.5f, dst[k]);
    }

    // ── stream-shape independence ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_output_does_not_depend_on_the_block_size_or_on_processing_in_place()
    {
        const int ch = 2, frames = 20_000;
        var src = Noise(frames, ch, 3f, 12345u);
        var whole = Run(new LimiterStage(mixRate: Rate, channels: ch), src, ch);

        foreach (int block in new[] { 1, 7, 64, 333, 4096 })
            Assert.True(whole.AsSpan().SequenceEqual(Run(new LimiterStage(mixRate: Rate, channels: ch), src, ch, block)),
                $"the output differs when processed {block} frames at a time");

        // In place (src and dst the same span) — what RenderMaster does: the delay line must store the INPUT, not the output.
        var inPlace = (float[])src.Clone();
        var lim = new LimiterStage(mixRate: Rate, channels: ch);
        for (int at = 0; at < frames; at += 500)
            lim.Process(inPlace.AsSpan(at * ch, 500 * ch), inPlace.AsSpan(at * ch, 500 * ch), 500, Ctx(ch));
        Assert.True(whole.AsSpan().SequenceEqual(inPlace), "processing in place changed the output");
    }

    [Fact]
    public void A_bypassed_limiter_is_an_undelayed_pass_through_and_does_not_replay_stale_audio()
    {
        const int ch = 1;
        var lim = new LimiterStage(mixRate: Rate, channels: ch);
        Run(lim, Const(300, 0.5f), ch);                          // leave audio in the delay line

        lim.Bypassed = true;
        Assert.Equal(0, lim.LatencySamples);
        var dry = Const(50, 0.3f);
        Assert.Equal(dry, Run(lim, dry, ch));                    // unchanged and undelayed

        lim.Bypassed = false;
        Assert.Equal(Look, lim.LatencySamples);
        var wet = Run(lim, Const(300, 0.1f), ch);
        for (int f = 0; f < Look; f++) Assert.Equal(0f, wet[f]);  // silence, not the 0.5 / 0.3 left behind
        Assert.Equal(0.1f, wet[Look]);
    }

    [Fact]
    public void Process_IsZeroAlloc()
    {
        const int ch = 2, block = 256;
        var lim = new LimiterStage(mixRate: Rate, channels: ch);
        var src = Noise(block, ch, 3f, 99u);
        var dst = new float[src.Length];
        for (int i = 0; i < 2000; i++) lim.Process(src, dst, block, Ctx(ch));   // warm the JIT
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++) lim.Process(src, dst, block, Ctx(ch));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ── the graph wiring ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compile_passes_the_channel_count_and_the_default_lookahead_to_the_master_limiter()
    {
        const int ch = 6, at = 20, frames = 300;
        var host = new AudioGraphHost(ch, Rate);
        var graph = host.Live;
        var limiter = Assert.IsType<LimiterStage>(graph.Master[^1]);
        Assert.Equal(Look, limiter.LatencySamples);
        Assert.Equal(Look, graph.TotalLatencySamples);   // the figure the position clock reads (the spectrum tap is post-limiter, so it does not)

        var buf = new float[frames * ch];
        buf[at * ch + 4] = 0.5f;                          // one impulse, on channel 4 only
        graph.RenderMaster(buf, frames, Ctx(ch));
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < ch; c++)
                Assert.Equal(f == at + Look && c == 4 ? 0.5f : 0f, buf[f * ch + c]);
    }

    [Fact]
    public void A_custom_LookaheadMs_is_the_graph_latency()
    {
        var host = new AudioGraphHost(2, Rate);
        host.Publish(AudioGraphSpec.Passthrough with { Limiter = new LimiterSpec(LookaheadMs: 5f) });
        Assert.Equal(240, host.Live.TotalLatencySamples);
    }

    [Fact]
    public void A_republish_with_an_unchanged_limiter_keeps_its_delay_line_and_a_changed_one_replaces_it()
    {
        const int at = 10, first = 50, second = 100;
        var host = new AudioGraphHost(1, Rate);
        var before = host.Live;
        var a = new float[first];
        a[at] = 0.5f;
        before.RenderMaster(a, first, Ctx(1));            // the impulse is now inside the delay line

        host.Publish(AudioGraphSpec.Passthrough with { MasterChain = ImmutableArray.Create<EffectSpec>(new GainSpec(1f)) });
        var after = host.Live;
        Assert.NotSame(before, after);
        Assert.Same(before.Master[^1], after.Master[^1]);   // the same limiter instance: no 2 ms hole on a republish
        var b = new float[second];
        after.RenderMaster(b, second, Ctx(1));
        for (int f = 0; f < second; f++) Assert.Equal(first + f == at + Look ? 0.5f : 0f, b[f]);

        host.Publish(AudioGraphSpec.Passthrough with { Limiter = new LimiterSpec(CeilingDbTp: -3f) });
        Assert.NotSame(after.Master[^1], host.Live.Master[^1]);
    }
}
