using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The gapless butt-join, at the mixer: a prepared voice added at the outgoing voice's natural-end frame with a CONSTANT
/// envelope must produce a SAMPLE-CONTINUOUS stream — no silent gap, no summed overlap, and the incoming side's encoder
/// priming trimmed away. Pins the shape Wavee's 0 ms hand-off relies on (docs/plans/wavee/gapless-findings.md §4) plus the
/// counter-example that explains why 0 ms must NOT be routed through the crossfade commit.
/// </summary>
public class GaplessJoinTests
{
    const int Rate = 48_000;
    const int Ch = 2;

    // A constant-amplitude block, so a gap reads as a zero run and an overlap reads as a doubled sample.
    static float[] Flat(int frames, int channels, float value)
    {
        var buf = new float[frames * channels];
        buf.AsSpan().Fill(value);
        return buf;
    }

    static float[] Render(CrossfadeMixer mixer, int frames)
    {
        var dst = new float[frames * Ch];
        mixer.Render(dst, frames, new BlockCtx(0, Rate, Ch, new ParamPlane()));
        return dst;
    }

    [Fact]
    public void Fade_WithZeroFrames_IsTheConstantEnvelope()
    {
        // THE guard rail behind the 0 ms design decision: feeding "no fade" through the fade builder yields unity gain,
        // so committing a 0 ms crossfade would leave BOTH voices at full level for the whole tail — dual-voice overlap,
        // not a butt-join. Anyone tempted to implement gapless as "crossfade with fadeMs = 0" has to delete this test.
        var env = GainEnvelope.Fade(FadeKind.Out, fadeStartFrame: 100, fadeFrames: 0, CrossCurve.Linear);

        Assert.Equal(FadeKind.None, env.Kind);
        Assert.Equal(1f, env.GainAt(0));
        Assert.Equal(1f, env.GainAt(100));
        Assert.Equal(1f, env.GainAt(10_000));
        Assert.Equal(0, GainEnvelope.Constant.FadeFrames);
    }

    [Fact]
    public void ConstantVoiceAtTheNaturalEndFrame_IsSampleContinuous()
    {
        const int len = 240;
        var mixer = new CrossfadeMixer(Ch, maxBlock: 1024);
        // A: 0.5 for its whole length. B: 1.0, starting exactly where A ends.
        mixer.AddVoice(new MixVoice
        {
            Id = 1,
            Src = new TrimmingSource(new MemoryAudioSource(Flat(len, Ch, 0.5f), Ch), GaplessInfo.None, Ch, len),
            Env = GainEnvelope.Constant,
            StartFrame = 0,
            ReplayGainScalar = 1f,
        });
        mixer.AddVoice(new MixVoice
        {
            Id = 2,
            Src = new TrimmingSource(new MemoryAudioSource(Flat(len, Ch, 1.0f), Ch), GaplessInfo.None, Ch, len),
            Env = GainEnvelope.Constant,
            StartFrame = len,          // the join: B's first frame is A's last frame + 1
            ReplayGainScalar = 1f,
        });

        var outBuf = Render(mixer, len * 2);

        // Every frame is sounded by exactly ONE voice: A's level, then B's level, with no zero frame between them and no
        // frame carrying A+B summed (which is what a fade-based "0 ms" commit would produce).
        for (int f = 0; f < len * 2; f++)
        {
            float expected = f < len ? 0.5f : 1.0f;
            Assert.Equal(expected, outBuf[f * Ch], 5);
            Assert.Equal(expected, outBuf[f * Ch + 1], 5);
        }
        Assert.DoesNotContain(0f, outBuf);                  // no silent gap at the seam
        Assert.DoesNotContain(1.5f, outBuf);                // no summed overlap at the seam
    }

    [Fact]
    public void TwoConstantVoicesOverlapping_SumTheirEnergy_WhichIsWhyZeroMsMustNotCrossfade()
    {
        // The counter-probe: the SAME two Constant voices with B started one block EARLY (what a crossfade commit with
        // fadeFrames = 0 effectively schedules) audibly sum instead of butting.
        const int len = 240;
        const int overlap = 60;
        var mixer = new CrossfadeMixer(Ch, maxBlock: 1024);
        mixer.AddVoice(new MixVoice
        {
            Id = 1,
            Src = new TrimmingSource(new MemoryAudioSource(Flat(len, Ch, 0.5f), Ch), GaplessInfo.None, Ch, len),
            Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f,
        });
        mixer.AddVoice(new MixVoice
        {
            Id = 2,
            Src = new TrimmingSource(new MemoryAudioSource(Flat(len, Ch, 1.0f), Ch), GaplessInfo.None, Ch, len),
            Env = GainEnvelope.Constant, StartFrame = len - overlap, ReplayGainScalar = 1f,
        });

        var outBuf = Render(mixer, len * 2);

        Assert.Equal(1.5f, outBuf[(len - overlap) * Ch], 5);   // A + B, both at unity
        Assert.Equal(1.5f, outBuf[(len - 1) * Ch], 5);
    }

    [Fact]
    public void TheJoinTrimsTheIncomingCodecPriming_SoTheSeamCarriesRealAudio()
    {
        // Encoder delay/padding is why a butt-join alone is not enough: B's leading priming frames must be trimmed, or
        // the seam plays them as audio. Here B's first `lead` frames are silence that TrimmingSource must drop.
        const int len = 240;
        const int lead = 32;
        var bRaw = Flat(len, Ch, 1.0f);
        bRaw.AsSpan(0, lead * Ch).Clear();                     // priming silence at the head of B

        var mixer = new CrossfadeMixer(Ch, maxBlock: 1024);
        mixer.AddVoice(new MixVoice
        {
            Id = 1,
            Src = new TrimmingSource(new MemoryAudioSource(Flat(len, Ch, 0.5f), Ch), GaplessInfo.None, Ch, len),
            Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f,
        });
        mixer.AddVoice(new MixVoice
        {
            Id = 2,
            Src = new TrimmingSource(
                new MemoryAudioSource(bRaw, Ch),
                new GaplessInfo(LeadInFrames: lead, TrailPadFrames: 0, ExactFrames: len - lead, TailKnown: true),
                Ch, len),
            Env = GainEnvelope.Constant, StartFrame = len, ReplayGainScalar = 1f,
        });

        var outBuf = Render(mixer, len * 2 - lead);

        Assert.Equal(0.5f, outBuf[(len - 1) * Ch], 5);   // A's last real frame …
        Assert.Equal(1.0f, outBuf[len * Ch], 5);         // … immediately followed by B's first POST-TRIM frame
    }
}
