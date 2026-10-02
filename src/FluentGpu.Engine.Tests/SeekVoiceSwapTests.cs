using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Playback smoothness D3 (WP-2a): the three ways the engine serves a seek WITHOUT ever stopping the device — the jump inside the
/// decode ring (<see cref="PcmAudioSession.TryJumpWithinRingAsync"/>, decided on the RT), the voice swap
/// (<see cref="PcmAudioSession.SwapToPreparedAsync"/>, one compound RT command) and the in-place hold-and-seek fallback
/// (<see cref="PcmAudioSession.SeekInPlaceAsync"/>) — plus the stale-voice fade and the pure pieces they stand on
/// (<see cref="JumpBlend"/>, <see cref="GainEnvelope"/> stamping, <see cref="IPreparedItem.IsReadyFor"/>, the mixer's by-reference voice slots).
/// <para>Everything drives the REAL session over a deterministic device (<see cref="BufferedAudioEndpoint"/>) with a hand-pumped
/// <see cref="AudioFeedThread"/>; the source is a RAMP — frame <c>f</c> carries the value <c>f × 1e-6</c> in both channels — so a captured
/// sample names the content frame it came from, and a seek that lands on the wrong frame, skips, repeats or blends wrongly shows up as a
/// wrong number. Where the code and the plan disagree the code wins: engine-issued voice ids start at 2^32 (not 1 000 000), and a
/// WSOLA-wrapped voice is held back <c>fade + 2 hops</c> at unity (not just <c>fade</c>) when the jump reach is decided.</para>
/// <para>The default master graph ends in a 2 ms lookahead limiter, which DELAYS the audio by
/// <see cref="CompiledAudioGraph.TotalLatencySamples"/> (96 frames at 48 kHz): the mixer frame submitted at device index <c>S</c> is heard at
/// <c>S + latency</c>, and every captured-sample expectation below carries that shift. The ramp stays under the limiter's ceiling, so it
/// is a pure delay.</para>
/// </summary>
public sealed class SeekVoiceSwapTests
{
    private static readonly MixFormat Fmt = new(48000, 2);
    private const int Block = 480;                 // 10 ms
    private const int Fade = 240;                  // the 5 ms equal-power pair every seek transition uses at 48 kHz
    private const int RampFrames = 480_000;        // 10 s

    private static readonly float[] Ramp = BuildRamp();

    /// <summary>The ramp value of content frame <paramref name="frame"/>.</summary>
    private static float V(long frame) => (float)(frame * 1e-6);

    /// <summary>The content frame a ramp value names.</summary>
    private static long FrameOf(float value) => (long)Math.Round(value * 1e6);

    private static float[] BuildRamp()
    {
        var data = new float[RampFrames * 2];
        for (int f = 0; f < RampFrames; f++) data[2 * f] = data[2 * f + 1] = V(f);
        return data;
    }

    private static float[] Ones(int frames)
    {
        var data = new float[frames * 2];
        Array.Fill(data, 1f);
        return data;
    }

    private static void Near(double expected, double actual, double tolerance, string what)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected:R}, got {actual:R} (±{tolerance})");

    // ── the rig ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One session on a 960-frame device with a hand-pumped feed (500 ms decoded ahead, 200 ms kept behind, a 1 s ring, 10 ms blocks);
    /// <see cref="Step"/> is 10 ms of wall time. The primary voice is the ramp from frame 0.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly BufferedAudioEndpoint Endpoint;
        public readonly PcmAudioSession Session;
        public readonly AudioFeedThread Feed;
        public readonly MediaPlayerCore Core = new();

        public Rig(bool play = true)
        {
            Endpoint = new BufferedAudioEndpoint(Fmt, capacityFrames: 960, captureFrames: 4 * 48000);
            Session = new PcmAudioSession(Fmt, Endpoint, Endpoint, Block, driveWithOwnThread: false);
            Feed = new AudioFeedThread(Session, Fmt.SampleRate, null, new RingSizing(BlockMs: 10.0, AheadMs: 500.0, RingMs: 1000.0, KeepBehindMs: 200.0));
            Session.SetVoice(new MemoryAudioSource(Ramp, 2), TimeSpan.FromSeconds(RampFrames / 48000.0), RampFrames, NormMode.Off, -14f, initialVolume: 1f);
            Session.ConnectSignals(new MediaSignalSink(Core));
            if (play) _ = Session.PlayAsync();
        }

        /// <summary>The master chain's own delay (the lookahead limiter): what the device hears <c>latency</c> frames after the mixer wrote it.</summary>
        public int Latency => Session.Graph.Live.TotalLatencySamples;

        /// <summary>10 ms: the worker decodes ahead (unless withheld), the RT renders one block, the device consumes one, the clock thread ticks.</summary>
        public void Step(bool pump = true)
        {
            if (pump) Feed.WorkerPumpOnce();
            Feed.FeedOnce();
            Endpoint.AdvanceHardware(Block);
            Session.TickControl(Block);
        }

        public void Run(int blocks, bool pump = true)
        {
            for (int i = 0; i < blocks; i++) Step(pump);
        }

        /// <summary>Play 60 blocks: steady state, the 20 ms fade-in long over, the whole kept-behind span intact.</summary>
        public void Warm()
        {
            Run(60);
            Assert.Equal(PlaybackState.Playing, Session.CurrentState);
            Assert.False(Session.IsStarved);
            Assert.Equal(0, Session.TransportPhase);
            Assert.Equal(Session.SubmittedFrames, Ring().PositionFrames);   // every submitted frame was read from the ring, once
        }

        /// <summary>The ring of the ACTIVE voice (the primary until a swap re-points transport).</summary>
        public RingAudioSource Ring()
        {
            foreach (var entry in Feed.RingsSnapshot)
                if (entry.VoiceId == Session.ActiveVoiceIdValue) return entry.Ring;
            throw new InvalidOperationException("The active voice has no published ring.");
        }

        public float[] Pcm() => Endpoint.Captured.ToArray();

        public void Dispose()
        {
            _ = Session.DisposeAsync();   // no feed threads were started: completes synchronously
            Endpoint.Dispose();
        }
    }

    // ── driving the async seek APIs against the hand-pumped RT ───────────────────────────────────────────────────────

    private static async Task<T> DriveTaskAsync<T>(Rig rig, Task<T> task, bool pump = true)
    {
        for (int i = 0; i < 2000 && !task.IsCompleted; i++) { rig.Step(pump); await Task.Delay(1); }
        return await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task DriveTaskAsync(Rig rig, Task task, bool pump = true)
    {
        for (int i = 0; i < 2000 && !task.IsCompleted; i++) { rig.Step(pump); await Task.Delay(1); }
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Task<T> DriveAsync<T>(Rig rig, ValueTask<T> op, bool pump = true) => DriveTaskAsync(rig, op.AsTask(), pump);

    private static Task DriveAsync(Rig rig, ValueTask op, bool pump = true) => DriveTaskAsync(rig, op.AsTask(), pump);

    /// <summary>Everything the tests ask of one mixer voice slot, read where a Span may live (never across an await).</summary>
    private readonly record struct VoiceInfo(bool Found, bool Held, long HoldAtFrame, long StartFrame, FadeKind Kind, long FadeStart, int FadeFrames, int BlendRemaining);

    private static VoiceInfo Inspect(PcmAudioSession session, long id)
    {
        foreach (var v in session.Mixer.VoicesSpan)
            if (v.Id == id) return new VoiceInfo(true, v.Held, v.HoldAtFrame, v.StartFrame, v.Env.Kind, v.Env.FadeStartFrame, v.Env.FadeFrames, v.Blend.Remaining);
        return default;
    }

    /// <summary>A prepared voice at <paramref name="startFrame"/> exactly as the app's pump builds one: a ring over a decoder already sought
    /// there (with the feed's own sizing), pre-filled, wrapped in an <see cref="AudioPreparedItem"/> whose start position is that frame.</summary>
    private static (RingAudioSource Ring, AudioPreparedItem Item) Prepare(long startFrame, IAudioSource? inner = null)
    {
        if (inner is null)
        {
            var memory = new MemoryAudioSource(Ramp, 2);
            memory.SeekFrame(startFrame);
            inner = memory;
        }
        var ring = new RingAudioSource(inner, 2, ringFrames: 48000, targetAheadFrames: 24000, pumpFrames: 2 * Block, keepBehindFrames: 9600, startFrames: startFrame);
        ring.PumpAhead();
        var item = new AudioPreparedItem(ring, GaplessInfo.None, default, RampFrames, TimeSpan.FromSeconds(RampFrames / 48000.0), Fmt.SampleRate, startPositionFrames: startFrame);
        return (ring, item);
    }

    // ── what the device heard ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The blend a jump (or a swap) leaves in the first <see cref="Fade"/> frames of its block: fresh × In + the old signal × Out.</summary>
    private static float BlendAt(int j, long freshFrame, long tailFrame, bool tailLive)
    {
        float p = (float)j / Fade;
        float tail = tailLive ? V(tailFrame) : 0f;
        return V(freshFrame) * CrossfadeCurves.In(CrossCurve.EqualPower, p) + tail * CrossfadeCurves.Out(CrossCurve.EqualPower, p);
    }

    /// <summary>The block whose first mixer frame was submitted at device index <paramref name="submitIndex"/>: a <see cref="Fade"/>-frame blend of
    /// the fresh voice (from <paramref name="freshStart"/>) and the old signal (from <paramref name="tailStart"/>), then the fresh voice alone,
    /// for <paramref name="frames"/> mixer frames in all. The device hears each mixer frame <paramref name="latency"/> frames after it was submitted.</summary>
    private static void AssertBlendedBlock(float[] pcm, long submitIndex, int latency, long freshStart, long tailStart, bool tailLive, int frames)
    {
        Assert.True((submitIndex + latency + frames) * 2 <= pcm.Length, "the device has not played that far yet");
        for (int j = 0; j < frames; j++)
        {
            float expected = j < Fade ? BlendAt(j, freshStart + j, tailStart + j, tailLive) : V(freshStart + j);
            long index = submitIndex + latency + j;
            Near(expected, pcm[index * 2], 2e-6, $"left sample {j} frames into the transition");
            Near(expected, pcm[index * 2 + 1], 2e-6, $"right sample {j} frames into the transition");
        }
    }

    /// <summary>The straight ramp, delayed by the limiter: device frame <c>k</c> holds the content frame <c>k − latency</c>.</summary>
    private static void AssertStraightRamp(float[] pcm, long fromIndex, long toIndex, int latency)
    {
        Assert.True(toIndex * 2 <= pcm.Length);
        for (long k = fromIndex; k < toIndex; k++)
            Near(V(k - latency), pcm[k * 2], 2e-6, $"device frame {k}");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // JumpBlend — the 5 ms equal-power blend a jump leaves in its voice's slot
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void JumpBlend_IsPowerComplementary_ContinuousWithTheOldSignal_AndExactlyOneFadeLong()
    {
        const int ch = 2;
        // Isolate each gain: the old signal alone (tail 1, fresh 0) leaves Out(p); the fresh signal alone (tail 0, fresh 1) leaves In(p).
        var tailOnes = new float[Fade * ch];
        Array.Fill(tailOnes, 1f);
        var outGain = new float[Fade * ch];
        var outBlend = JumpBlend.Start(tailOnes, Fade, ch);
        Assert.Equal(Fade, outBlend.Remaining);
        outBlend.Apply(outGain, Fade);
        Assert.Equal(0, outBlend.Remaining);          // a finished blend clears itself: the slot never blends again
        Assert.Null(outBlend.Tail);

        var inGain = new float[Fade * ch];
        Array.Fill(inGain, 1f);
        var inBlend = JumpBlend.Start(new float[Fade * ch], Fade, ch);
        inBlend.Apply(inGain, Fade);

        Near(1.0, outGain[0], 1e-6, "frame 0 is the old signal exactly");
        Near(0.0, inGain[0], 1e-6, "and carries none of the new one");
        for (int i = 0; i < Fade; i++)
        {
            float o = outGain[i * ch], n = inGain[i * ch];
            Near(1.0, o * o + n * n, 1e-6, $"cos² + sin² at frame {i}");          // power-complementary: no level dip or bump through the cross
            Assert.Equal(o, outGain[i * ch + 1]);                                    // both channels get the same gain
            Assert.Equal(n, inGain[i * ch + 1]);
            if (i > 0)
            {
                Assert.True(o <= outGain[(i - 1) * ch], $"Out is not falling at frame {i}");
                Assert.True(n >= inGain[(i - 1) * ch], $"In is not rising at frame {i}");
            }
        }
        Assert.True(outGain[(Fade - 1) * ch] < 0.01f);   // the old signal has all but gone by the last blended frame
    }

    [Fact]
    public void JumpBlend_AppliedInPieces_EqualsAppliedAtOnce_AndAnEmptyBlendTouchesNothing()
    {
        const int ch = 2;
        var tail = new float[Fade * ch];
        for (int i = 0; i < tail.Length; i++) tail[i] = 0.25f + i * 1e-3f;
        float Fresh(int i) => 0.9f - i * 1e-3f;

        var whole = new float[Fade * ch];
        for (int i = 0; i < whole.Length; i++) whole[i] = Fresh(i);
        var all = JumpBlend.Start(tail, Fade, ch);
        all.Apply(whole, Fade);

        // the same blend consumed block by block (100 + 100 + 40 frames), as the mixer does when a block is shorter than the fade
        var pieces = new float[Fade * ch];
        for (int i = 0; i < pieces.Length; i++) pieces[i] = Fresh(i);
        var blend = JumpBlend.Start(tail, Fade, ch);
        int offset = 0;
        foreach (int n in new[] { 100, 100, 40 })
        {
            Assert.True(blend.Remaining > 0);
            blend.Apply(pieces.AsSpan(offset * ch, n * ch), n);
            offset += n;
        }
        Assert.Equal(0, blend.Remaining);
        for (int i = 0; i < whole.Length; i++) Assert.Equal(whole[i], pieces[i]);

        // a default blend is "no blend pending": applying it changes nothing
        var untouched = new float[8];
        Array.Fill(untouched, 0.5f);
        JumpBlend none = default;
        Assert.Equal(0, none.Remaining);
        none.Apply(untouched, 4);
        Assert.All(untouched, v => Assert.Equal(0.5f, v));
    }

    [Fact]
    public void JumpBlend_StartAndApply_AreAllocationFree()
    {
        var tail = new float[Fade * 2];
        var fresh = new float[Fade * 2];
        var warm = JumpBlend.Start(tail, Fade, 2);
        warm.Apply(fresh, Fade);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            var blend = JumpBlend.Start(tail, Fade, 2);
            blend.Apply(fresh, 100);
            blend.Apply(fresh, 140);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // GainEnvelope — unstamped shells, stamped by the RT exactly once
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GainEnvelope_UnstampedShells_ShareOneTable_AndAreStampedIndependently()
    {
        var template = GainEnvelope.FadeUnstamped(FadeKind.In, Fade, CrossCurve.EqualPower);
        Assert.Equal(GainEnvelope.Unstamped, template.FadeStartFrame);
        Assert.Equal(Fade, template.FadeFrames);
        Assert.Equal(FadeKind.In, template.Kind);

        var a = template.NewUnstamped();
        var b = template.NewUnstamped();
        Assert.NotSame(a, b);
        Assert.NotSame(template, a);
        Assert.True(a.Lut.Overlaps(b.Lut), "a seek storm must not rebuild the lookup table: every shell shares the template's");
        Assert.False(a.Lut.Overlaps(GainEnvelope.FadeUnstamped(FadeKind.In, Fade, CrossCurve.EqualPower).Lut), "an independently built envelope has its own table");

        Assert.Same(a, a.StampStart(1000));                       // the RT stamps and installs the very instance it was handed
        Assert.Equal(1000, a.FadeStartFrame);
        Assert.Equal(GainEnvelope.Unstamped, b.FadeStartFrame);   // a sibling shell and the template stay unstamped
        Assert.Equal(GainEnvelope.Unstamped, template.FadeStartFrame);

        Assert.Equal(0f, a.GainAt(999));                          // before the window: pinned at the starting level
        Assert.Equal(0f, a.GainAt(1000));                         // In starts at silence …
        Near(MathF.Sin(MathF.PI / 4), a.GainAt(1000 + Fade / 2), 1e-6, "halfway through the equal-power In");
        Assert.Equal(1f, a.GainAt(1000 + Fade));                  // … and is at unity as the window ends
        Assert.Equal(1f, a.GainAt(5000));
    }

    [Fact]
    public void GainEnvelope_StampedPair_IsPowerComplementary_AndPinnedOutsideTheWindow()
    {
        var fadeIn = GainEnvelope.FadeUnstamped(FadeKind.In, Fade, CrossCurve.EqualPower).StampStart(480);
        var fadeOut = GainEnvelope.FadeUnstamped(FadeKind.Out, Fade, CrossCurve.EqualPower).StampStart(480);

        for (int i = 0; i < Fade; i++)
        {
            float g = fadeIn.GainAt(480 + i), o = fadeOut.GainAt(480 + i);
            Near(1.0, g * g + o * o, 1e-6, $"In² + Out² at frame {i}");
        }
        Assert.Equal(1f, fadeOut.GainAt(479));                    // an Out that has not started is at unity
        Assert.Equal(0f, fadeOut.GainAt(480 + Fade));             // and one that has finished is silent
        Assert.Equal(0f, fadeIn.GainAt(479));
        Assert.Equal(1f, fadeIn.GainAt(480 + Fade));
    }

    [Fact]
    public void GainEnvelope_ConstantAndDegenerateFades_IgnoreStamping()
    {
        var constant = GainEnvelope.Constant;
        Assert.Same(constant, constant.NewUnstamped());                                                    // nothing to stamp, nothing to copy
        Assert.Same(constant, constant.StampStart(500));
        Assert.Equal(0, constant.FadeStartFrame);                                                          // the shared singleton is never mutated
        Assert.Equal(1f, constant.GainAt(0));
        Assert.Equal(1f, constant.GainAt(123_456));

        Assert.Same(constant, GainEnvelope.FadeUnstamped(FadeKind.None, Fade, CrossCurve.EqualPower));
        Assert.Same(constant, GainEnvelope.FadeUnstamped(FadeKind.In, 0, CrossCurve.EqualPower));
    }

    [Fact]
    public void GainEnvelope_StampStart_IsAllocationFree()
    {
        var shell = GainEnvelope.FadeUnstamped(FadeKind.Out, Fade, CrossCurve.EqualPower);
        shell.StampStart(1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) shell.StampStart(i);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // CrossfadeMixer — voice slots by reference, parked voices
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void RenderBlock(CrossfadeMixer mixer, float[] dst, int frames = Block)
    {
        var ctx = new BlockCtx(mixer.ConsumeSeq, 48000, 2, new ParamPlane());
        mixer.Render(dst, frames, in ctx);
    }

    [Fact]
    public void CrossfadeMixer_RendersByReference_SoAJumpBlendProgressesAcrossBlocks()
    {
        var mixer = new CrossfadeMixer(2, Block);
        // fresh signal = 1.0; the old signal's tail = silence: the blend output is exactly In(p)
        mixer.AddVoice(new MixVoice
        {
            Id = 5, Src = new MemoryAudioSource(Ones(4 * Block), 2), Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f,
            Blend = JumpBlend.Start(new float[Fade * 2], Fade, 2),
        });
        var dst = new float[Block * 2];

        RenderBlock(mixer, dst, 120);                                           // a block SHORTER than the fade: the blend is half done
        Assert.Equal(120, mixer.VoiceRef(5).Blend.Done);
        Assert.Equal(Fade - 120, mixer.VoiceRef(5).Blend.Remaining);            // the progress lives in the slot the mixer iterates, not in a copy
        for (int i = 0; i < 120; i++) Near(CrossfadeCurves.In(CrossCurve.EqualPower, (float)i / Fade), dst[i * 2], 1e-6, $"frame {i}");

        RenderBlock(mixer, dst, 240);                                           // the rest of the blend, then the plain voice
        Assert.Equal(0, mixer.VoiceRef(5).Blend.Remaining);
        Assert.Null(mixer.VoiceRef(5).Blend.Tail);
        for (int i = 0; i < 120; i++) Near(CrossfadeCurves.In(CrossCurve.EqualPower, (float)(120 + i) / Fade), dst[i * 2], 1e-6, $"frame {120 + i}");
        for (int i = 120; i < 240; i++) Assert.Equal(1f, dst[i * 2]);
    }

    [Fact]
    public void CrossfadeMixer_VoiceRef_IsTheLiveSlot_AndANullReferenceForAnUnknownVoice()
    {
        var mixer = new CrossfadeMixer(2, Block);
        mixer.AddVoice(new MixVoice { Id = 9, Src = new MemoryAudioSource(Ones(Block), 2), Env = GainEnvelope.Constant, ReplayGainScalar = 1f });

        ref MixVoice slot = ref mixer.VoiceRef(9);
        slot.ReplayGainScalar = 0.25f;
        slot.Held = true;
        Assert.Equal(0.25f, mixer.VoiceRef(9).ReplayGainScalar);                // a write through the reference persists (a foreach copy would lose it)
        Assert.True(mixer.VoiceRef(9).Held);

        Assert.True(mixer.HasVoice(9));
        Assert.False(mixer.HasVoice(10));
        Assert.True(System.Runtime.CompilerServices.Unsafe.IsNullRef(ref mixer.VoiceRef(10)));
    }

    [Fact]
    public void CrossfadeMixer_ParkedVoice_IsNeverReadNeverRetired_UntilAnEnvelopeRestoresIt()
    {
        // The stale-seek fade parks the old voice: its own 48-frame fade-out carries it to silence BEFORE HoldAtFrame, so parking is inaudible.
        var src = new MemoryAudioSource(Ones(8 * Block), 2);
        var mixer = new CrossfadeMixer(2, Block);
        mixer.AddVoice(new MixVoice
        {
            Id = 5, Src = src, ReplayGainScalar = 1f, Held = true, HoldAtFrame = 48,
            Env = GainEnvelope.FadeUnstamped(FadeKind.Out, 48, CrossCurve.EqualPower).StampStart(0),
        });
        var dst = new float[Block * 2];

        RenderBlock(mixer, dst);                                                // [0, 480): the fade-out plays, then the voice sounds silent
        Assert.Equal(Block, src.PositionFrames);
        Near(1.0, dst[0], 1e-6, "the fade-out starts at unity");
        Assert.Equal(0f, dst[48 * 2]);
        Assert.Equal(1, mixer.VoiceCount);                                      // its Out window has passed, yet it is NOT retired: it is parked

        RenderBlock(mixer, dst);                                                // [480, 960): parked — never read, never mixed
        RenderBlock(mixer, dst);
        Assert.Equal(Block, src.PositionFrames);
        Assert.All(dst, v => Assert.Equal(0f, v));
        Assert.Equal(1, mixer.VoiceCount);
        Assert.False(mixer.IsDrained(mixer.ConsumeSeq));                        // a parked voice is not "finished": the mixer is not drained, so playback never reads as Ended

        // installing an envelope ENDS the hold: the voice resumes from where it was left, under the new fade-in
        long at = mixer.ConsumeSeq;
        Assert.True(mixer.TrySetVoiceEnvelope(5, GainEnvelope.FadeUnstamped(FadeKind.In, 48, CrossCurve.EqualPower).StampStart(at)));
        Assert.False(mixer.VoiceRef(5).Held);
        RenderBlock(mixer, dst);
        Assert.Equal(2 * Block, src.PositionFrames);
        Assert.Equal(0f, dst[0]);
        Assert.Equal(1f, dst[48 * 2]);

        // the contrast: the same voice WITHOUT the hold is retired the moment its fade-out has passed
        var plain = new CrossfadeMixer(2, Block);
        plain.AddVoice(new MixVoice
        {
            Id = 5, Src = new MemoryAudioSource(Ones(8 * Block), 2), ReplayGainScalar = 1f,
            Env = GainEnvelope.FadeUnstamped(FadeKind.Out, 48, CrossCurve.EqualPower).StampStart(0),
        });
        RenderBlock(plain, dst);
        Assert.Equal(0, plain.VoiceCount);
    }

    [Fact]
    public void CrossfadeMixer_ParkedRingVoice_IsNeverWaitedFor_AndNeverNeedsACushion()
    {
        // A ring that is dry and still producing stalls the whole mixer … unless its voice is parked: nobody hears it, so nobody waits for it.
        var ring = new RingAudioSource(new MemoryAudioSource(Ones(100_000), 2), 2, ringFrames: 4096, targetAheadFrames: 2048, pumpFrames: 512);
        var mixer = new CrossfadeMixer(2, Block);
        mixer.AddVoice(new MixVoice { Id = 7, Src = ring, Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f });

        Assert.Equal(0, mixer.ReadableFrames(Block, out var waitingFor));
        Assert.Same(ring, waitingFor);
        Assert.False(mixer.PcmReady(Block));

        ref MixVoice slot = ref mixer.VoiceRef(7);
        slot.Held = true;
        slot.HoldAtFrame = 0;                                                    // the hold has taken effect
        Assert.Equal(Block, mixer.ReadableFrames(Block, out waitingFor));
        Assert.Null(waitingFor);
        Assert.True(mixer.PcmReady(Block));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // IPreparedItem.IsReadyFor / AudioPreparedItem ownership
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An endless-until-gated source: <see cref="Available"/> is the cumulative number of frames it may have produced.</summary>
    private sealed class GatedSource(int available) : IAudioSource
    {
        public int Available = available;
        private long _position;
        public long PositionFrames => Volatile.Read(ref _position);
        public bool Exhausted => false;
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
        public int Read(Span<float> dst, int channels)
        {
            long room = Available - Volatile.Read(ref _position);
            int frames = (int)Math.Max(0, Math.Min(dst.Length / channels, room));
            dst[..(frames * channels)].Fill(0.25f);
            Interlocked.Add(ref _position, frames);
            return frames;
        }
    }

    /// <summary>A source that counts how many times it was disposed (the ring disposes its inner exactly once, at its own disposal).</summary>
    private sealed class CountingSource : IAudioSource, IDisposable
    {
        private readonly MemoryAudioSource _inner = new(Ramp, 2);
        public int Disposals;
        public CountingSource(long startFrame) => _inner.SeekFrame(startFrame);
        public long PositionFrames => _inner.PositionFrames;
        public bool Exhausted => _inner.Exhausted;
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
        public int Read(Span<float> dst, int channels) => _inner.Read(dst, channels);
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    private sealed class FakePrepared : IPreparedItem
    {
        public bool Ready;
        public MediaKind Kind => MediaKind.PcmAudio;
        public bool IsReady => Ready;
        public IAudioSource? AudioVoice => null;
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
        public long TotalFrames => -1;
        public TimeSpan Duration => TimeSpan.Zero;
        public object? BackendHandle => null;
        public int MixRate => 0;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static AudioPreparedItem ItemOver(RingAudioSource ring)
        => new(ring, GaplessInfo.None, default, 100_000, TimeSpan.FromSeconds(2), Fmt.SampleRate);

    [Fact]
    public void IsReadyFor_NeedsOnlyTheFramesAskedFor_NotTheFullDecodeAheadCushion()
    {
        // 600 frames produced against a 4096-frame target: IsReady wants the whole cushion, a seek swap needs ONE block.
        var gate = new GatedSource(600);
        var ring = new RingAudioSource(gate, 2, ringFrames: 8192, targetAheadFrames: 4096, pumpFrames: 512);
        var item = ItemOver(ring);
        Assert.False(item.IsReady);
        Assert.False(item.IsReadyFor(1));                                        // nothing pumped yet
        ring.PumpAhead();
        Assert.Equal(600, ring.BufferedFrames);

        Assert.False(item.IsReady);
        Assert.True(item.IsReadyFor(Block));
        Assert.True(item.IsReadyFor(600));
        Assert.False(item.IsReadyFor(601));
        Assert.True(item.IsReadyFor(0));                                         // a request for nothing still needs SOME real PCM (≥ 1 frame)

        gate.Available = 1_000_000;
        ring.PumpAhead();                                                        // the ring fills to its target: now both agree
        Assert.True(item.IsReady);
        Assert.True(item.IsReadyFor(4096));
    }

    [Fact]
    public void IsReadyFor_AFinishedProducerWithSomePcm_IsReady_ButAnEmptyFinishedOneIsNot()
    {
        var shortRing = new RingAudioSource(new MemoryAudioSource(Ones(300), 2), 2, ringFrames: 8192, targetAheadFrames: 4096, pumpFrames: 512);
        shortRing.PumpAhead();
        Assert.True(shortRing.ProducerDone);
        var shortItem = ItemOver(shortRing);
        Assert.True(shortItem.IsReadyFor(Block));                                // 300 < 480 buffered, but the producer is done: waiting longer cannot help
        Assert.True(shortItem.IsReady);

        var emptyRing = new RingAudioSource(new MemoryAudioSource(Array.Empty<float>(), 2), 2, ringFrames: 8192, targetAheadFrames: 4096, pumpFrames: 512);
        emptyRing.PumpAhead();
        Assert.True(emptyRing.ProducerDone);
        var emptyItem = ItemOver(emptyRing);
        Assert.False(emptyItem.IsReadyFor(1));                                   // nothing to swap in
        Assert.False(emptyItem.IsReady);
    }

    [Fact]
    public void IsReadyFor_IsFalseWhileASeekFlushIsPending_BecauseWhatTheRingHoldsIsPreSeekAudio()
    {
        var ring = new RingAudioSource(new MemoryAudioSource(Ramp, 2), 2, ringFrames: 8192, targetAheadFrames: 4096, pumpFrames: 512);
        ring.PumpAhead();
        var item = ItemOver(ring);
        Assert.True(item.IsReadyFor(Block));

        ring.WorkerApplySeek(10_000);                                            // the decoder has moved; the RT has not discarded the old PCM yet
        Assert.True(ring.HasPendingFlush);
        Assert.True(ring.BufferedFrames > 0);
        Assert.False(item.IsReadyFor(1));
        Assert.False(ring.IsReady(1));

        ring.RtConsumeFlush();                                                   // flushed: the ring is empty, still not ready
        Assert.False(item.IsReadyFor(1));
        ring.PumpAhead();                                                        // refilled from the new position
        Assert.True(item.IsReadyFor(Block));
        Assert.Equal(10_000, ring.PositionFrames);
    }

    [Fact]
    public void IsReadyFor_ForANonRingVoiceAndTheInterfaceDefault_FollowsTheVoiceAndIsReady()
    {
        var live = new AudioPreparedItem(new MemoryAudioSource(new float[2000], 2), GaplessInfo.None, default, 1000, TimeSpan.FromSeconds(1), Fmt.SampleRate);
        Assert.True(live.IsReadyFor(1_000_000));                                 // a voice with no PCM depth to speak of: ready while it is not exhausted
        var spent = new AudioPreparedItem(new MemoryAudioSource(Array.Empty<float>(), 2), GaplessInfo.None, default, 0, TimeSpan.Zero, Fmt.SampleRate);
        Assert.False(spent.IsReadyFor(1));

        // a backend that does not override IsReadyFor answers IsReady
        IPreparedItem notReady = new FakePrepared { Ready = false };
        IPreparedItem ready = new FakePrepared { Ready = true };
        Assert.False(notReady.IsReadyFor(1));
        Assert.True(ready.IsReadyFor(int.MaxValue));
    }

    [Fact]
    public async Task AudioPreparedItem_DisposeReleasesTheVoiceExactlyOnce_AndNeverAfterOwnershipTransfers()
    {
        var owned = new CountingSource(0);
        var item = ItemOver(new RingAudioSource(owned, 2, ringFrames: 8192, targetAheadFrames: 4096, pumpFrames: 512));
        await item.DisposeAsync();
        await item.DisposeAsync();                                               // a cancelled prepare is disposed by whoever finds it — twice is harmless
        Assert.Equal(1, owned.Disposals);

        var handedOver = new CountingSource(0);
        var transferred = ItemOver(new RingAudioSource(handedOver, 2, ringFrames: 8192, targetAheadFrames: 4096, pumpFrames: 512));
        transferred.TransferOwnership();                                         // the mixer owns the ring now
        await transferred.DisposeAsync();
        Assert.Equal(0, handedOver.Disposals);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Seek B — the jump inside the ring: the RT decides, with an exact reach
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Task<bool> JumpAsync(Rig rig, long target)
        => DriveAsync(rig, rig.Session.TryJumpWithinRingAsync(target, CancellationToken.None), pump: false);

    // Unity WSOLA voice: the tail read can consume `fade` ring frames and the voice holds back two analysis hops on top.
    private static int UnitySlack => Fade + 2 * (Fmt.SampleRate / 50);

    // A stretched voice pulls its analysis window ahead of its output: fade × 3 + 8 hops + 2 search margins.
    private static int StretchedSlack => Fade * 3 + (Fmt.SampleRate / 50) * 8 + (Fmt.SampleRate / 100) * 2;

    [Fact]
    public async Task TryJumpWithinRing_WithoutAnRtFeed_IsRefused()
    {
        using var endpoint = new BufferedAudioEndpoint(Fmt, 960, 48000);
        var session = new PcmAudioSession(Fmt, endpoint, endpoint, Block, false);
        session.SetVoice(new MemoryAudioSource(Ramp, 2), TimeSpan.FromSeconds(10), RampFrames, NormMode.Off, -14f, 1f);

        Assert.False(session.IsRtDriven);
        Assert.False(await session.TryJumpWithinRingAsync(1000, CancellationToken.None));   // no ring, no RT to decide: the dispatcher falls back
        _ = session.DisposeAsync();
    }

    [Theory]
    [InlineData(0, true)]       // exactly the lowest reachable frame
    [InlineData(-1, false)]     // one frame earlier: the tail read would eat into the rewind
    public async Task Jump_WhileAudible_ReachesBackExactlyAsFarAsTheKeptBehindSpanMinusTheTailSlack(int offset, bool accepted)
    {
        using var rig = new Rig();
        rig.Warm();
        var ring = rig.Ring();
        Assert.Equal(9600, ring.KeptBehindFrames);                                   // the whole 200 ms kept-behind span is intact after 60 blocks

        // The listener hears the old voice, so the RT first reads the tail the blend needs (up to `fade` ring frames, plus the voice's two
        // analysis hops of lookahead are held back): the lowest reachable frame is pos + slack − kept.
        long target = ring.PositionFrames + UnitySlack - ring.KeptBehindFrames + offset;
        Assert.True(target > 0);

        Assert.Equal(accepted, await JumpAsync(rig, target));
        Assert.False(rig.Session.SwapLanded);                                        // a jump is not a swap
    }

    [Theory]
    [InlineData(0, true)]       // exactly the decoded ahead span minus one fade and one block
    [InlineData(1, false)]
    public async Task Jump_WhileAudible_ReachesForwardExactlyAsFarAsTheDecodedSpanMinusOneFadeAndOneBlock(int offset, bool accepted)
    {
        using var rig = new Rig();
        rig.Warm();
        var ring = rig.Ring();

        long target = ring.PositionFrames + ring.BufferedFrames - Fade - Block + offset;
        Assert.Equal(accepted, await JumpAsync(rig, target));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    public async Task Jump_WhilePaused_ReachesBackTheWholeKeptBehindSpan_BecauseThereIsNoTailToRead(int offset, bool accepted)
    {
        using var rig = new Rig();
        rig.Warm();
        await DriveAsync(rig, rig.Session.PauseAsync(), pump: false);                // nothing is audible now: the device is stopped
        Assert.Equal(3, rig.Session.TransportPhase);
        var ring = rig.Ring();
        long pos = ring.PositionFrames;
        Assert.Equal(9600, ring.KeptBehindFrames);

        long target = pos - ring.KeptBehindFrames + offset;
        Assert.Equal(accepted, await JumpAsync(rig, target));

        if (accepted)
        {
            Assert.Equal(target, ring.PositionFrames);                               // nothing renders while paused: the cursor IS the target
            Assert.Equal(0, ring.KeptBehindFrames);                                  // and the kept-behind span is spent
            Assert.Equal(0, Inspect(rig.Session, rig.Session.ActiveVoiceIdValue).BlendRemaining);   // nobody hears the jump: no blend is left behind
            Assert.Equal(TimeSpan.FromSeconds((double)target / Fmt.SampleRate), rig.Core.Position.Peek());   // the position follows at once
        }
        else
        {
            Assert.Equal(pos, ring.PositionFrames);                                  // a refusal moves nothing
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Jump_WhilePaused_ReachesForwardExactlyAsFarAsTheDecodedSpanMinusOneFadeAndOneBlock(int offset, bool accepted)
    {
        using var rig = new Rig();
        rig.Warm();
        await DriveAsync(rig, rig.Session.PauseAsync(), pump: false);
        var ring = rig.Ring();

        long target = ring.PositionFrames + ring.BufferedFrames - Fade - Block + offset;
        Assert.Equal(accepted, await JumpAsync(rig, target));
        if (accepted) Assert.Equal(target, ring.PositionFrames);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public async Task Jump_OnAStretchedVoice_HoldsBackTheWholeLookaheadWindowAsSlack(int offset, bool accepted)
    {
        using var rig = new Rig();
        rig.Warm();
        rig.Session.SetRate(1.5);                                                     // the voice now pulls an analysis window ahead of its output
        rig.Run(40);
        var ring = rig.Ring();
        Assert.Equal(9600, ring.KeptBehindFrames);

        long target = ring.PositionFrames + StretchedSlack - ring.KeptBehindFrames + offset;
        Assert.True(target > 0);
        Assert.Equal(accepted, await JumpAsync(rig, target));
    }

    [Fact]
    public async Task Jump_BlendsFiveMsEqualPower_ThenPlaysTheTargetExactly_AndRebasesThePosition()
    {
        using var rig = new Rig();
        rig.Warm();
        var ring = rig.Ring();
        long pos = ring.PositionFrames;
        long submitted = rig.Session.SubmittedFrames;                                // the jump block is written at this device index (the device took every block)
        Assert.Equal(pos, submitted);
        long target = pos + 5000;

        Assert.True(await JumpAsync(rig, target));
        rig.Run(6, pump: false);                                                     // let the hardware play everything out
        int latency = rig.Latency;
        var pcm = rig.Pcm();

        // before the jump block the device is still playing the old position, delayed by the limiter
        AssertStraightRamp(pcm, submitted - 960, submitted + latency, latency);
        // then: the old position's next 5 ms × Out ⊕ the target's × In, and the target alone after it — for the jump block AND the next one
        AssertBlendedBlock(pcm, submitted, latency, freshStart: target, tailStart: pos, tailLive: true, frames: 2 * Block);

        // the position is re-anchored at the submit index of the jump block: target + (what has played since) − the master chain's own delay
        long played = rig.Session.PlayedFrames;
        long expected = Math.Max(0, target + (played - submitted) - rig.Session.PositionTracker.ExtraLatencySamples);
        Assert.Equal(expected, rig.Session.PositionTracker.PlayedFramesCompensated);
        Assert.True(played > submitted);
    }

    [Fact]
    public async Task RefusedJump_MovesNothing_TheAudioIsTheStraightRampThroughIt()
    {
        using var rig = new Rig();
        rig.Warm();
        var ring = rig.Ring();
        long submitted = rig.Session.SubmittedFrames;
        long tooFar = ring.PositionFrames + ring.BufferedFrames;                     // beyond the decoded span

        Assert.False(await JumpAsync(rig, tooFar));
        Assert.False(rig.Session.SwapLanded);
        Assert.Equal(0, Inspect(rig.Session, rig.Session.ActiveVoiceIdValue).BlendRemaining);
        rig.Run(4, pump: false);

        var pcm = rig.Pcm();
        AssertStraightRamp(pcm, submitted - 960, pcm.Length / 2, rig.Latency);        // no skip, no repeat, no blend — the old audio plays straight through
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Seek A — the voice swap: one compound RT command, the old voice at unity until the swap block
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SwapToPrepared_CrossfadesTheOldVoiceUnderAnEqualPowerPairAtTheNextBlock_AndReturnsTheAchievedFrame()
    {
        using var rig = new Rig();
        rig.Warm();
        var oldRing = rig.Ring();
        long pos = oldRing.PositionFrames;
        long submitted = rig.Session.SubmittedFrames;
        long consume = rig.Session.Mixer.ConsumeSeq;
        const long T = 200_000;
        var (ring, item) = Prepare(T);

        long achieved = await DriveAsync(rig, rig.Session.SwapToPreparedAsync(item, seekGeneration: 7, CancellationToken.None));

        Assert.Equal(T, achieved);                                                    // StartPositionFrames, the achieved CONTENT frame
        Assert.True(rig.Session.SwapLanded);
        Assert.Equal(7, rig.Session.LastSeekGeneration);
        long id = rig.Session.ActiveVoiceIdValue;
        Assert.True(id >= 1L << 32, "engine-issued voice ids start at 2^32, disjoint from the app's and the scheduler's");
        Assert.NotEqual(rig.Session.PrimaryVoiceIdValue, id);

        // the incoming voice starts at the first frame of the block the command landed in, its In envelope stamped by the RT at that frame
        var voice = Inspect(rig.Session, id);
        Assert.True(voice.Found);
        Assert.Equal(consume, voice.StartFrame);
        Assert.Equal(FadeKind.In, voice.Kind);
        Assert.Equal(consume, voice.FadeStart);
        Assert.Equal(Fade, voice.FadeFrames);

        rig.Run(8);                                                                   // the old voice's Out window passes inside the swap block; the worker then retires it
        Assert.False(rig.Session.Mixer.HasVoice(rig.Session.PrimaryVoiceIdValue));
        Assert.Equal(1, rig.Feed.RingCount);
        Assert.Same(ring, rig.Feed.RingsSnapshot[0].Ring);
        Assert.True(ring.BufferedFrames > 20_000, "the swapped-in ring is published to the feed and pumped like the primary");

        int latency = rig.Latency;
        var pcm = rig.Pcm();
        // the old voice plays at unity right up to the swap block …
        AssertStraightRamp(pcm, submitted - 960, submitted + latency, latency);
        // … then one 5 ms equal-power pair from the block's FIRST frame, and the new position alone after it: no block rendered with half a swap
        AssertBlendedBlock(pcm, submitted, latency, freshStart: T, tailStart: pos, tailLive: true, frames: 2 * Block);

        long played = rig.Session.PlayedFrames;
        long expected = Math.Max(0, T + (played - submitted) - rig.Session.PositionTracker.ExtraLatencySamples);
        Assert.Equal(expected, rig.Session.PositionTracker.PlayedFramesCompensated);
    }

    [Fact]
    public async Task SwapToPrepared_WhilePaused_IsACut_TheOldVoiceGoesAtOnce_AndPlaybackResumesAtTheNewPosition()
    {
        using var rig = new Rig();
        rig.Warm();
        await DriveAsync(rig, rig.Session.PauseAsync(), pump: false);
        Assert.Equal(PlaybackState.Paused, rig.Session.CurrentState);
        long consume = rig.Session.Mixer.ConsumeSeq;
        const long T = 300_000;
        var (ring, item) = Prepare(T);

        Assert.Equal(T, await DriveAsync(rig, rig.Session.SwapToPreparedAsync(item, 1, CancellationToken.None)));   // a swap applies with the device stopped

        long id = rig.Session.ActiveVoiceIdValue;
        Assert.False(rig.Session.Mixer.HasVoice(rig.Session.PrimaryVoiceIdValue));    // nothing was audible: no pair, the old voice is gone this block
        var voice = Inspect(rig.Session, id);
        Assert.True(voice.Found);
        Assert.Equal(consume, voice.StartFrame);
        Assert.Equal(FadeKind.In, voice.Kind);
        Assert.Equal(TimeSpan.FromSeconds((double)T / Fmt.SampleRate), rig.Core.Position.Peek());
        Assert.Equal(PlaybackState.Paused, rig.Session.CurrentState);
        Assert.Equal(1, rig.Endpoint.StopCount);                                      // the pause's stop; the swap never touched the device

        // resume: the first thing that plays is the new voice — the ring cursor moves on from T, and the last sample out is the ramp, delayed
        await rig.Session.PlayAsync();
        rig.Run(40);
        Assert.Equal(PlaybackState.Playing, rig.Session.CurrentState);
        Assert.True(ring.PositionFrames > T + 10 * Block);
        var pcm = rig.Pcm();
        long last = rig.Session.SubmittedFrames - 1;
        Near(V(ring.PositionFrames - 1 - rig.Latency), pcm[last * 2], 2e-6, "the last device frame is the new position's ramp, delayed by the limiter");
    }

    [Fact]
    public async Task SwapToPrepared_AfterTheStaleFade_ReplacesTheParkedVoice_AndTheNewAudioFadesInFromSilence()
    {
        using var rig = new Rig();
        rig.Warm();
        // the prepare is late: the pump's 80 ms stale timer fades the old voice — still playing a position the listener has left — to silence
        await DriveAsync(rig, rig.Session.FadeActiveToSilenceAsync(TimeSpan.FromMilliseconds(80)));
        rig.Run(12);
        var parked = Inspect(rig.Session, rig.Session.PrimaryVoiceIdValue);
        Assert.True(parked.Held);
        long submitted = rig.Session.SubmittedFrames;
        const long T = 150_000;
        var (_, item) = Prepare(T);

        Assert.Equal(T, await DriveAsync(rig, rig.Session.SwapToPreparedAsync(item, 2, CancellationToken.None)));
        Assert.False(rig.Session.Mixer.HasVoice(rig.Session.PrimaryVoiceIdValue));    // the parked voice is replaced outright (a cut: it is silent already)
        rig.Run(6);

        int latency = rig.Latency;
        var pcm = rig.Pcm();
        for (long k = submitted; k < submitted + latency; k++) Assert.Equal(0f, pcm[k * 2]);   // the delayed tail of the parked voice: silence
        AssertBlendedBlock(pcm, submitted, latency, freshStart: T, tailStart: 0, tailLive: false, frames: 2 * Block);   // no old voice to cross with: fresh × In only
    }

    [Fact]
    public async Task SwapToPrepared_RejectsAVoiceAtAnotherRate_LeavingTheLiveVoiceAndTheGateUntouched()
    {
        using var rig = new Rig();
        rig.Warm();
        var counting = new CountingSource(0);
        var ring = new RingAudioSource(counting, 2, ringFrames: 8192, targetAheadFrames: 4096, pumpFrames: 512);
        ring.PumpAhead();
        var item = new AudioPreparedItem(ring, GaplessInfo.None, default, RampFrames, TimeSpan.FromSeconds(10), mixRate: 44100);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await rig.Session.SwapToPreparedAsync(item, 1, CancellationToken.None));

        Assert.False(rig.Session.SwapLanded);
        Assert.Equal(rig.Session.PrimaryVoiceIdValue, rig.Session.ActiveVoiceIdValue);
        Assert.True(rig.Session.Mixer.HasVoice(rig.Session.PrimaryVoiceIdValue));
        Assert.Equal(1, rig.Feed.RingCount);                                          // the rejected ring was never published
        Assert.True(await JumpAsync(rig, rig.Ring().PositionFrames + 3000));          // the replacement gate was never taken, let alone leaked
        await item.DisposeAsync();                                                    // the caller still owns it, and releases it exactly once
        Assert.Equal(1, counting.Disposals);
    }

    [Fact]
    public async Task SwapToPrepared_CancelledBeforeItIsAdmitted_LeavesTheLiveVoiceUntouched_AndTheItemDisposableExactlyOnce()
    {
        using var rig = new Rig();
        rig.Warm();
        var counting = new CountingSource(100_000);
        var (ring, item) = Prepare(100_000, counting);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rig.Session.SwapToPreparedAsync(item, 1, cts.Token));

        Assert.False(rig.Session.SwapLanded);
        Assert.True(rig.Session.Mixer.HasVoice(rig.Session.PrimaryVoiceIdValue));
        Assert.Equal(1, rig.Feed.RingCount);
        Assert.True(await JumpAsync(rig, rig.Ring().PositionFrames + 3000));          // the gate is free
        Assert.Equal(0, counting.Disposals);                                          // the engine never disposed what it never admitted
        await item.DisposeAsync();
        await item.DisposeAsync();
        Assert.Equal(1, counting.Disposals);
        Assert.NotNull(ring);
    }

    [Fact]
    public async Task SwapToPrepared_DisposingTheItemAfterwards_NeverTouchesTheRingTheMixerNowOwns()
    {
        using var rig = new Rig();
        rig.Warm();
        var counting = new CountingSource(120_000);
        var (ring, item) = Prepare(120_000, counting);

        Assert.Equal(120_000, await DriveAsync(rig, rig.Session.SwapToPreparedAsync(item, 1, CancellationToken.None)));
        await item.DisposeAsync();                                                    // the pump disposes the prepared item once the swap has landed
        Assert.Equal(0, counting.Disposals);

        rig.Run(10);
        Assert.Equal(0, counting.Disposals);
        Assert.True(ring.PositionFrames > 120_000 + 5 * Block);                       // and it is the ring that is playing
        Assert.Same(ring, rig.Ring());
    }

    [Fact]
    public void NextEngineVoiceId_IsMonotonic_AboveEveryIdTheAppAndTheSchedulerHandOut()
    {
        using var rig = new Rig(play: false);
        long a = rig.Session.NextEngineVoiceId(), b = rig.Session.NextEngineVoiceId();
        Assert.Equal(a + 1, b);
        Assert.True(a >= 1L << 32);
        Assert.NotEqual(rig.Session.PrimaryVoiceIdValue, a);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // The stale-voice fade and "SwapLanded is not sticky"
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FadeActiveToSilence_FadesTheOldVoiceOverTheDuration_ThenParksIt_NeverRetiringOrReadingIt()
    {
        using var rig = new Rig();
        rig.Warm();
        var ring = rig.Ring();
        long pos = ring.PositionFrames;
        long submitted = rig.Session.SubmittedFrames;
        long consume = rig.Session.Mixer.ConsumeSeq;
        const int fadeFrames = 3840;                                                  // 80 ms

        await DriveAsync(rig, rig.Session.FadeActiveToSilenceAsync(TimeSpan.FromMilliseconds(80)), pump: false);

        var voice = Inspect(rig.Session, rig.Session.PrimaryVoiceIdValue);
        Assert.True(voice.Found);
        Assert.True(voice.Held);
        Assert.Equal(consume + fadeFrames, voice.HoldAtFrame);                         // parked once its own fade-out has finished
        Assert.Equal(FadeKind.Out, voice.Kind);
        Assert.Equal(consume, voice.FadeStart);                                        // stamped by the RT at the block the command landed in
        Assert.Equal(fadeFrames, voice.FadeFrames);
        Assert.False(rig.Session.SwapLanded);

        rig.Run(14, pump: false);
        long frozen = ring.PositionFrames;
        rig.Run(5, pump: false);
        Assert.Equal(frozen, ring.PositionFrames);                                     // a parked voice is never read again
        Assert.True(rig.Session.Mixer.HasVoice(rig.Session.PrimaryVoiceIdValue));     // and never retired: its ring and byte source stay alive for the prepare
        Assert.False(rig.Session.IsStarved);                                           // nor waited for: the mixer keeps rendering silence, not a starve
        Assert.Equal(0, rig.Session.XrunCount);

        int latency = rig.Latency;
        var pcm = rig.Pcm();
        for (int j = 0; j < fadeFrames; j += 120)
        {
            float gain = CrossfadeCurves.Out(CrossCurve.EqualPower, (float)j / fadeFrames);
            Near(V(pos + j) * gain, pcm[(submitted + latency + j) * 2], 2e-6, $"the fade-out {j} frames in");
        }
        for (long k = submitted + latency + fadeFrames; k < pcm.Length / 2; k++) Assert.Equal(0f, pcm[k * 2]);   // silent from the end of the fade on
    }

    [Fact]
    public async Task FadeActiveToSilence_QueuedBehindALandingSwap_SeesSwapLandedAndDoesNothing()
    {
        using var rig = new Rig();
        rig.Warm();
        const long T = 200_000;
        var (_, item) = Prepare(T);

        // the swap takes the replacement gate; the stale timer's fade queues behind it (the timer fired just as the prepare produced its block)
        Task<long> swap = rig.Session.SwapToPreparedAsync(item, 3, CancellationToken.None).AsTask();
        Task fade = rig.Session.FadeActiveToSilenceAsync(TimeSpan.FromMilliseconds(80)).AsTask();
        Assert.False(fade.IsCompleted);

        Assert.Equal(T, await DriveTaskAsync(rig, swap));
        await DriveTaskAsync(rig, fade);

        Assert.True(rig.Session.SwapLanded);
        long id = rig.Session.ActiveVoiceIdValue;
        Assert.False(Inspect(rig.Session, id).Held);                                   // the NEW voice was not silenced
        rig.Run(20);
        Assert.False(Inspect(rig.Session, id).Held);
        var pcm = rig.Pcm();
        long last = rig.Session.SubmittedFrames - 1;
        Assert.NotEqual(0f, pcm[last * 2]);                                            // and it is audible
    }

    [Fact]
    public async Task SwapLanded_IsNotStickyAcrossSeeks_EachNewSeekClearsItBeforeAnythingIsApplied()
    {
        using var rig = new Rig();
        rig.Warm();
        Assert.False(rig.Session.SwapLanded);

        Assert.Equal(200_000, await DriveAsync(rig, rig.Session.SwapToPreparedAsync(Prepare(200_000).Item, 1, CancellationToken.None)));
        Assert.True(rig.Session.SwapLanded);

        // a jump begins: the flag is cleared at the call, so a stale timer racing this seek cannot read the PREVIOUS seek's landing
        Task<bool> jump = rig.Session.TryJumpWithinRingAsync(rig.Ring().PositionFrames + 3000, CancellationToken.None).AsTask();
        Assert.False(rig.Session.SwapLanded);
        Assert.True(await DriveTaskAsync(rig, jump, pump: false));
        Assert.False(rig.Session.SwapLanded);                                          // and a jump never sets it

        Assert.Equal(250_000, await DriveAsync(rig, rig.Session.SwapToPreparedAsync(Prepare(250_000).Item, 2, CancellationToken.None)));
        Assert.True(rig.Session.SwapLanded);
        Assert.Equal(2, rig.Session.LastSeekGeneration);

        Task<long> inPlace = rig.Session.SeekInPlaceAsync(400_000, CancellationToken.None).AsTask();
        Assert.False(rig.Session.SwapLanded);                                          // the in-place fallback clears it too
        Assert.Equal(400_000, await DriveTaskAsync(rig, inPlace));
        Assert.False(rig.Session.SwapLanded);

        Assert.Equal(300_000, await DriveAsync(rig, rig.Session.SwapToPreparedAsync(Prepare(300_000).Item, 3, CancellationToken.None)));
        Assert.True(rig.Session.SwapLanded);
        Task seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(1), SeekMode.Accurate).AsTask();
        Assert.False(rig.Session.SwapLanded);                                          // the IMediaSession dispatcher clears it as well
        await DriveTaskAsync(rig, seek);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // The in-place seek: hold at silence, flush, seek, fade back in — never Stop/Reset
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeekInPlace_WhilePlaying_HoldsAtSilence_NeverStopsTheDevice_AndTheSeekRebufferIsNotAnIncident()
    {
        using var rig = new Rig();
        rig.Warm();
        const long T = 250_000;
        Assert.Equal(4800, rig.Session.ResumeFrames);

        long achieved = await DriveAsync(rig, rig.Session.SeekInPlaceAsync(T, CancellationToken.None));
        rig.Run(12);

        Assert.Equal(T, achieved);
        Assert.Equal(0, rig.Endpoint.StopCount);                                       // the device is never stopped …
        Assert.Equal(0, rig.Endpoint.ResetCount);                                      // … or reset
        Assert.Equal(1, rig.Endpoint.StartCount);
        Assert.Equal(0, rig.Session.TransportPhase);                                   // the hold (phase 4) was released: the transport runs again
        Assert.False(rig.Session.IsStarved);
        Assert.Equal(PlaybackState.Playing, rig.Session.CurrentState);
        Assert.Equal(4800, rig.Session.ResumeFrames);                                  // a seek rebuffer never grows the starvation cushion
        Assert.False(rig.Session.SwapLanded);

        var ring = rig.Ring();
        Assert.True(ring.PositionFrames > T + 5 * Block, "the post-seek audio is playing");
        // the position is re-anchored where the first post-seek CONTENT block was written (silence never moves the content clock): within a block of exact
        long expected = ring.PositionFrames - rig.Session.PositionTracker.ExtraLatencySamples;
        Assert.InRange(rig.Session.PositionTracker.PlayedFramesCompensated, expected - Block, expected + Block);
        var pcm = rig.Pcm();
        long last = rig.Session.SubmittedFrames - 1;
        Near(V(ring.PositionFrames - 1 - rig.Latency), pcm[last * 2], 2e-6, "the last device frame is the post-seek ramp");
    }

    [Fact]
    public async Task SeekInPlace_WhilePaused_DegradesToAPlainFlushAndSeek_WithNoFadeAndNoHoldToStrandTheTransport()
    {
        using var rig = new Rig();
        rig.Warm();
        await DriveAsync(rig, rig.Session.PauseAsync(), pump: false);
        Assert.Equal(3, rig.Session.TransportPhase);
        Assert.Equal(1, rig.Endpoint.StopCount);                                       // the pause's own stop
        const long T = 123_456;

        long achieved = await DriveAsync(rig, rig.Session.SeekInPlaceAsync(T, CancellationToken.None));

        Assert.Equal(T, achieved);
        Assert.Equal(PlaybackState.Paused, rig.Session.CurrentState);                  // the user paused: a seek does not resume
        Assert.Equal(3, rig.Session.TransportPhase);                                   // no hold fade was posted — a fade on a stopped device would strand phase 1
        Assert.Equal(1, rig.Endpoint.StopCount);
        Assert.Equal(1, rig.Endpoint.StartCount);
        Assert.Equal(0, rig.Endpoint.ResetCount);
        var ring = rig.Ring();
        Assert.Equal(T, ring.PositionFrames);                                          // the decoder seeked and the RT flushed the pre-seek PCM
        Assert.True(ring.IsReady(Block));                                              // refilled from the new position, waiting for play
        Assert.Equal(TimeSpan.FromSeconds((double)T / Fmt.SampleRate), rig.Core.Position.Peek());
        Assert.False(rig.Session.SwapLanded);
    }

    [Fact]
    public async Task SeekInPlace_BeforePlaybackHasStarted_DegradesTheSameWay_AndNeverStartsTheDevice()
    {
        using var rig = new Rig(play: false);
        rig.Run(5);                                                                    // opened and buffered, never played
        Assert.False(rig.Endpoint.IsStarted);
        const long T = 96_000;

        long achieved = await DriveAsync(rig, rig.Session.SeekInPlaceAsync(T, CancellationToken.None));

        Assert.Equal(T, achieved);
        Assert.Equal(0, rig.Endpoint.StartCount);
        Assert.Equal(0, rig.Endpoint.StopCount);
        Assert.Equal(0, rig.Endpoint.ResetCount);
        Assert.Equal(0, rig.Session.TransportPhase);                                   // untouched: no hold was ever requested
        Assert.Equal(T, rig.Ring().PositionFrames);
        Assert.True(rig.Ring().IsReady(Block));
        Assert.Equal(TimeSpan.FromSeconds((double)T / Fmt.SampleRate), rig.Core.Position.Peek());
    }

    [Fact]
    public async Task SeekInPlace_SupersededByANewerSeek_ReturnsWithoutCompleting_AndTheNewerSeekEndsTheHold()
    {
        using var rig = new Rig();
        rig.Warm();
        const long first = 100_000, second = 350_000;

        Task<long> a = rig.Session.SeekInPlaceAsync(first, CancellationToken.None).AsTask();    // takes the gate and posts the hold fade
        Task<long> b = rig.Session.SeekInPlaceAsync(second, CancellationToken.None).AsTask();   // newer: supersedes A at once, waits its turn on the gate
        Assert.False(b.IsCompleted);

        Assert.Equal(first, await DriveTaskAsync(rig, a));                              // A noticed it was superseded and gave the hold to B untouched
        Assert.Equal(second, await DriveTaskAsync(rig, b));
        rig.Run(12);

        Assert.Equal(0, rig.Session.TransportPhase);                                    // the hold B inherited was released: not stranded at silence
        Assert.Equal(0, rig.Endpoint.StopCount);
        Assert.False(rig.Session.IsStarved);
        Assert.Equal(PlaybackState.Playing, rig.Session.CurrentState);
        Assert.True(rig.Ring().PositionFrames > second);                                // playing from the NEWER target, never from the first
        Assert.True(rig.Ring().PositionFrames < second + 40 * Block);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Zero-alloc pins on the RT arms (the allocations of building a command stay on the control side)
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bytes THIS thread allocates while it applies the posted command as the RT would: one <see cref="AudioFeedThread.FeedOnce"/> —
    /// the command drain (the arm under test) and the block that follows it. The control side's own allocations happen before the clock starts.</summary>
    private static long RtAllocatedBy(Rig rig)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        rig.Feed.FeedOnce();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        rig.Endpoint.AdvanceHardware(Block);
        return allocated;
    }

    [Fact]
    public async Task TheJumpArm_OnTheRt_IsAllocationFree()
    {
        using var rig = new Rig();
        rig.Warm();
        Assert.True(await JumpAsync(rig, rig.Ring().PositionFrames + 2000));            // warm: JIT every path the arm and its blend take
        rig.Run(3);

        Task<bool> op = rig.Session.TryJumpWithinRingAsync(rig.Ring().PositionFrames + 2000, CancellationToken.None).AsTask();
        long allocated = RtAllocatedBy(rig);
        Assert.True(await DriveTaskAsync(rig, op));

        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task TheSwapArm_OnTheRt_IsAllocationFree()
    {
        using var rig = new Rig();
        rig.Warm();
        Assert.Equal(200_000, await DriveAsync(rig, rig.Session.SwapToPreparedAsync(Prepare(200_000).Item, 1, CancellationToken.None)));   // warm
        rig.Run(4);
        var (_, item) = Prepare(260_000);

        Task<long> op = rig.Session.SwapToPreparedAsync(item, 2, CancellationToken.None).AsTask();
        long allocated = RtAllocatedBy(rig);                                             // the swap block also retires the old voice's ring to the worker
        Assert.Equal(260_000, await DriveTaskAsync(rig, op));

        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task TheSilenceArm_OnTheRt_IsAllocationFree()
    {
        using var rig = new Rig();
        rig.Warm();
        await DriveAsync(rig, rig.Session.FadeActiveToSilenceAsync(TimeSpan.FromMilliseconds(40)));   // warm
        rig.Run(8);
        Assert.True(rig.Session.SetVoiceEnvelope(rig.Session.ActiveVoiceIdValue, GainEnvelope.Constant));   // an envelope on a parked voice restores it
        rig.Run(2);
        Assert.False(Inspect(rig.Session, rig.Session.ActiveVoiceIdValue).Held);

        Task op = rig.Session.FadeActiveToSilenceAsync(TimeSpan.FromMilliseconds(40)).AsTask();
        long allocated = RtAllocatedBy(rig);
        await DriveTaskAsync(rig, op);

        Assert.Equal(0, allocated);
        Assert.True(Inspect(rig.Session, rig.Session.ActiveVoiceIdValue).Held);
    }
}
