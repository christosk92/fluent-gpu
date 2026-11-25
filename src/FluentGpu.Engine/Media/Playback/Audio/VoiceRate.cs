using System;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>
/// #48 E4: one voice's OWN rate over the mixer timeline, as a factor on the session's rate (<see cref="PcmAudioSession.PlaybackRate"/>,
/// the podcast speed): the voice plays at <c>session rate × factor</c>. A constant factor, or a factor that holds until
/// <see cref="GlideStartFrame"/> and then moves to <see cref="Target"/> in steps of <see cref="Step"/>, one step every
/// <see cref="StepIntervalFrames"/>; a remaining difference smaller than a step goes in one step (the official player's beatmatch
/// glide: 0.901696 → 0.906696 → … → 0.996696 → 1, 20 steps a second apart). Step k (1-based) lands on mixer frame
/// <c>GlideStartFrame + (k − 1) × StepIntervalFrames</c>, exactly: the voice's synthesis splits its hop there.
/// <para>Immutable, built off the render thread; the render thread only reads it. Frames are mixer frames
/// (<see cref="PcmAudioSession.SampleClock"/>), the domain a crossfade voice's start frame and its envelopes live in.</para>
/// </summary>
public sealed class VoiceRatePlan
{
    /// <summary>The smallest factor a plan accepts.</summary>
    public const double MinFactor = 0.5;
    /// <summary>The largest factor a plan accepts.</summary>
    public const double MaxFactor = 2.0;
    /// <summary>The official player's glide step (0.005 of rate, one step a second).</summary>
    public const double DefaultGlideStep = 0.005;

    private readonly double _sign;

    private VoiceRatePlan(double initial, long glideStart, double target, double step, long interval, int steps)
    {
        Initial = initial;
        GlideStartFrame = glideStart;
        Target = target;
        Step = step;
        StepIntervalFrames = interval;
        StepCount = steps;
        _sign = target >= initial ? 1 : -1;
    }

    /// <summary>The factor from the voice's first frame until the glide's first step.</summary>
    public double Initial { get; }
    /// <summary>The factor the glide ends on (equal to <see cref="Initial"/> for a constant plan).</summary>
    public double Target { get; }
    /// <summary>The mixer frame of the glide's first step; <see cref="long.MaxValue"/> for a constant plan.</summary>
    public long GlideStartFrame { get; }
    /// <summary>The factor change per step (positive).</summary>
    public double Step { get; }
    /// <summary>Mixer frames between two steps.</summary>
    public long StepIntervalFrames { get; }
    /// <summary>Steps from <see cref="Initial"/> to <see cref="Target"/> (0 for a constant plan).</summary>
    public int StepCount { get; }
    /// <summary>True when the factor never changes.</summary>
    public bool IsConstant => StepCount == 0;
    /// <summary>The largest factor the plan ever plays (the read-ahead a stretched voice needs is sized by it).</summary>
    public double MaxFactorValue => Math.Max(Initial, Target);

    /// <summary>A constant factor. <paramref name="factor"/> is clamped to [<see cref="MinFactor"/>, <see cref="MaxFactor"/>].</summary>
    public static VoiceRatePlan Constant(double factor)
    {
        factor = Clamp(factor);
        return new VoiceRatePlan(factor, long.MaxValue, factor, DefaultGlideStep, 1, 0);
    }

    /// <summary>Hold <paramref name="from"/>, then glide to <paramref name="to"/>: the first step at mixer frame
    /// <paramref name="glideStartFrame"/>, then one step of <paramref name="step"/> every <paramref name="stepIntervalFrames"/>
    /// frames; a remainder under one step goes in one step.</summary>
    public static VoiceRatePlan Glide(double from, long glideStartFrame, double to, double step, long stepIntervalFrames)
    {
        from = Clamp(from);
        to = Clamp(to);
        if (!(step > 0) || !double.IsFinite(step)) throw new ArgumentOutOfRangeException(nameof(step));
        if (stepIntervalFrames <= 0) throw new ArgumentOutOfRangeException(nameof(stepIntervalFrames));
        double diff = Math.Abs(to - from);
        if (diff == 0) return Constant(from);
        int steps = (int)Math.Ceiling(diff / step - 1e-9);
        return new VoiceRatePlan(from, glideStartFrame, to, step, stepIntervalFrames, Math.Max(1, steps));
    }

    private static double Clamp(double factor)
        => double.IsFinite(factor) ? Math.Clamp(factor, MinFactor, MaxFactor) : 1.0;

    /// <summary>The factor after <paramref name="k"/> steps (0 = <see cref="Initial"/>; <see cref="StepCount"/> and beyond = <see cref="Target"/>).</summary>
    public double StepValue(int k)
        => k <= 0 ? Initial : k >= StepCount ? Target : Initial + _sign * k * Step;

    /// <summary>The mixer frame step <paramref name="k"/> (1-based) lands on.</summary>
    public long StepFrame(int k) => k <= 0 || StepCount == 0 ? long.MinValue : GlideStartFrame + (k - 1) * StepIntervalFrames;

    /// <summary>Steps taken at mixer frame <paramref name="mixerFrame"/> (a step landing on that frame counts).</summary>
    public int StepsAt(long mixerFrame)
    {
        if (StepCount == 0 || mixerFrame < GlideStartFrame) return 0;
        long k = 1 + (mixerFrame - GlideStartFrame) / StepIntervalFrames;
        return (int)Math.Min(StepCount, k);
    }

    /// <summary>The factor at mixer frame <paramref name="mixerFrame"/>.</summary>
    public double FactorAt(long mixerFrame) => StepValue(StepsAt(mixerFrame));

    /// <summary>The first mixer frame after <paramref name="mixerFrame"/> at which the factor changes; <see cref="long.MaxValue"/> when it never does again.</summary>
    public long NextChangeAfter(long mixerFrame)
    {
        int k = StepsAt(mixerFrame);
        return k >= StepCount ? long.MaxValue : StepFrame(k + 1);
    }

    /// <summary>The voice's effective rate at a factor: <c>session × factor</c>, clamped as the stretch clamps it.</summary>
    public static double EffectiveRate(double sessionRate, double factor) => WsolaAudioSource.ClampRate(sessionRate * factor);

    /// <summary>Source frames the voice advances between mixer frames <paramref name="fromMixerFrame"/> and <paramref name="toMixerFrame"/>
    /// (exact: the sum over the plan's constant stretches). B's fade-in window in B's media time is
    /// <c>SourceFramesBetween(start, start + overlap)</c> (= overlap × speedB while the glide has not begun).</summary>
    public double SourceFramesBetween(long fromMixerFrame, long toMixerFrame, double sessionRate = 1.0)
    {
        if (toMixerFrame <= fromMixerFrame) return 0;
        double acc = 0;
        long cur = fromMixerFrame;
        while (cur < toMixerFrame)
        {
            long next = Math.Min(toMixerFrame, NextChangeAfter(cur));
            acc += (next - cur) * EffectiveRate(sessionRate, FactorAt(cur));
            cur = next;
        }
        return acc;
    }

    /// <summary>The mixer frames the voice needs from <paramref name="fromMixerFrame"/> to advance <paramref name="sourceFrames"/> source
    /// frames (the inverse of <see cref="SourceFramesBetween"/>). Fractional: the caller rounds.</summary>
    public double MixerFramesFor(double sourceFrames, long fromMixerFrame, double sessionRate = 1.0)
    {
        if (!(sourceFrames > 0)) return 0;
        double elapsed = 0, left = sourceFrames;
        long cur = fromMixerFrame;
        while (true)
        {
            double rate = EffectiveRate(sessionRate, FactorAt(cur));
            long next = NextChangeAfter(cur);
            if (next == long.MaxValue) return elapsed + left / rate;
            double span = (next - cur) * rate;
            if (span >= left) return elapsed + left / rate;
            left -= span;
            elapsed += next - cur;
            cur = next;
        }
    }
}

/// <summary>#48 E4: what a voice's renderer last applied of its <see cref="VoiceRatePlan"/>. Read it on a control tick: a changed
/// <see cref="Changes"/> means a step happened since the last read (the app then publishes <see cref="Factor"/> to Connect).</summary>
/// <param name="Factor">The voice's own factor at its newest synthesized frame (the renderer runs ahead of the ear by the device buffer).</param>
/// <param name="EffectiveRate">Session rate × <see cref="Factor"/>, clamped: the voice's source frames per output frame.</param>
/// <param name="Changes">How many times the factor in effect changed (the first frame at a non-unity factor counts as one).</param>
/// <param name="LastChangeMixerFrame">The mixer frame the newest change took effect at; <see cref="long.MinValue"/> before any.</param>
/// <param name="Resampled">True while the newest frames were resampled (a factor within <see cref="WsolaAudioSource.ResampleBelow"/> of 1)
/// rather than stretched.</param>
public readonly record struct VoiceRateReading(double Factor, double EffectiveRate, long Changes, long LastChangeMixerFrame, bool Resampled);

public sealed partial class PcmAudioSession
{
    // #48 E4: the plan each voice was last given (control side; guarded by _mixerCmdProducerLock). A glide starts from the factor
    // the current plan has at its start frame; a design-A seek swap carries the active voice's plan onto the swapped-in voice.
    private readonly System.Collections.Generic.Dictionary<long, VoiceRatePlan> _ratePlans = new();

    /// <summary>#48 E4: give voice <paramref name="id"/> its own rate <paramref name="plan"/> (a factor on the session rate), or
    /// back to 1 with <c>null</c>. The render thread adopts it at the next block boundary and the voice's next synthesis hop plays it
    /// (the frames it has already synthesized, at most one 20 ms hop, keep the old factor); every step of a plan armed ahead of
    /// its steps (the glide) lands on its exact mixer frame. Works on a voice added in the same breath (commands apply in order).
    /// False when the command could not be admitted (the queue is full): retry or cancel. A voice id that is not live is ignored.
    /// Control thread. To start a voice stretched, pass the plan to
    /// <see cref="TryAddCrossfadeVoice(IAudioSource, GainEnvelope, long, float, IDspStage[], long, VoiceRatePlan)"/> instead.</summary>
    public bool SetVoiceRatePlan(long id, VoiceRatePlan? plan)
    {
        if (plan is not null) WsolaAudioSource.EnsureResampleKernel();   // the near-unity kernel is built here, never on the render thread
        lock (_mixerCmdProducerLock)
        {
            if (!TryEnqueueMixerCmd(new MixerCmd { Kind = CmdSetRatePlan, Id = id, RatePlan = plan }, out _)) return false;
            if (plan is null) _ratePlans.Remove(id); else _ratePlans[id] = plan;
            return true;
        }
    }

    /// <summary>#48 E4: voice <paramref name="id"/> plays at <paramref name="factor"/> × the session rate from the next block on.</summary>
    public bool SetVoiceRate(long id, double factor) => SetVoiceRatePlan(id, VoiceRatePlan.Constant(factor));

    /// <summary>#48 E4: voice <paramref name="id"/> holds its current factor and then glides to <paramref name="toFactor"/>: the first
    /// step lands on mixer frame <paramref name="atFrame"/>, then one step of <paramref name="step"/> every
    /// <paramref name="stepIntervalFrames"/> (0 = one second at the mix rate). The glide starts from the factor the voice's current
    /// plan has at <paramref name="atFrame"/> (1 without a plan).</summary>
    public bool SetVoiceRateGlide(long id, long atFrame, double toFactor, double step = VoiceRatePlan.DefaultGlideStep, long stepIntervalFrames = 0)
    {
        double from;
        lock (_mixerCmdProducerLock) from = _ratePlans.TryGetValue(id, out var current) ? current.FactorAt(atFrame) : 1.0;
        long interval = stepIntervalFrames > 0 ? stepIntervalFrames : _format.SampleRate;
        return SetVoiceRatePlan(id, VoiceRatePlan.Glide(from, atFrame, toFactor, step, interval));
    }

    /// <summary>#48 E4: the plan voice <paramref name="id"/> was last given, or null.</summary>
    public VoiceRatePlan? VoiceRatePlanOf(long id)
    {
        lock (_mixerCmdProducerLock) return _ratePlans.TryGetValue(id, out var plan) ? plan : null;
    }

    /// <summary>#48 E4: what voice <paramref name="id"/>'s renderer last applied (cheap: a few volatile reads). False when the session
    /// does not know the voice. Control thread (the thread that adds voices and calls <see cref="SetActiveVoice"/>).</summary>
    public bool TryReadVoiceRate(long id, out VoiceRateReading reading)
    {
        if (_rateSources.TryGetValue(id, out var source)) { reading = source.ReadRate(); return true; }
        reading = default;
        return false;
    }

    /// <summary>#48 E4: the source frame voice <paramref name="id"/> plays at mixer frame <paramref name="mixerFrame"/>: from what it
    /// has rendered (its output → source map) for a frame already rendered, and along its rate plan beyond that, or from its start
    /// frame for a voice that has not started. The content-position ↔ mixer-frame map the app schedules a stretched voice by.
    /// Control thread.</summary>
    public bool TrySourceFrameAtMixerFrame(long id, long mixerFrame, out double sourceFrame)
    {
        sourceFrame = 0;
        if (!_rateSources.TryGetValue(id, out var source)) return false;
        long start = _voiceStarts.TryGetValue(id, out long s) ? s : 0;
        sourceFrame = source.SourceFrameAtMixer(mixerFrame, start, PlaybackRate);
        return true;
    }

    /// <summary>#48 E4: the mixer frame at which voice <paramref name="id"/> reaches source frame <paramref name="sourceFrame"/> (the
    /// inverse of <see cref="TrySourceFrameAtMixerFrame"/>; rounded to the nearest frame). Control thread.</summary>
    public bool TryMixerFrameAtSourceFrame(long id, double sourceFrame, out long mixerFrame)
    {
        mixerFrame = 0;
        if (!_rateSources.TryGetValue(id, out var source)) return false;
        long start = _voiceStarts.TryGetValue(id, out long s) ? s : 0;
        mixerFrame = (long)Math.Round(source.MixerFrameAtSource(sourceFrame, start, PlaybackRate));
        return true;
    }

    /// <summary>The rate of the active voice at clock frame <paramref name="clockFrame"/> (the audible frame): the session rate
    /// unless the voice has its own plan, then the rate its output → source map recorded for that frame.</summary>
    private double ActiveRateAt(double clockFrame)
    {
        var source = Volatile.Read(ref _activeRateSource);
        if (source is null || !source.HasRatePlan) return PlaybackRate;
        return source.RateAt(Math.Max(0, clockFrame - _clockAnchorPosition - _activeMixerStart));
    }
}
