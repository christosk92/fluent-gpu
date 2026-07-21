using System;
using System.Collections.Immutable;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// M2 — the audio-graph POD shapes (spec §7.2/§7.4/§7.5). The fixed internal mix format is f32 interleaved, device-rate,
// stereo (MixFormat lives in MediaSeams.cs). Everything below is the pull-graph vocabulary: the per-block context, the
// uniform DSP-stage + leaf-source node shapes, the immutable published graph value, and the smoothed param plane.
//
// Threading (M2 = SINGLE-THREAD-CORRECT): the control thread compiles + publishes a graph and pumps the mixer; the
// M4 flip moves the pump onto the WASAPI RT feed thread with NO shape change here.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Per-block pull context handed to every <see cref="IDspStage.Process"/> / voice mix (spec §7.2). A ref struct —
/// it is never boxed and never escapes the block. <c>f32</c> interleaved is implied by <see cref="MixFormat"/>.</summary>
public readonly ref struct BlockCtx
{
    /// <summary>The absolute mixer-domain frame index at the START of this block (drives §7.6 position + §8.3 joins).</summary>
    public readonly long StartFrame;
    /// <summary>The device mix rate (Hz).</summary>
    public readonly int MixRate;
    /// <summary>The channel count (2 = stereo — the fixed internal layout).</summary>
    public readonly int Channels;
    /// <summary>The smoothed parameter plane (spec §7.5) — topology is swapped, params are ramped.</summary>
    public readonly ParamPlane Params;

    /// <summary>Create a block context.</summary>
    public BlockCtx(long startFrame, int mixRate, int channels, ParamPlane @params)
    {
        StartFrame = startFrame;
        MixRate = mixRate;
        Channels = channels;
        Params = @params;
    }
}

/// <summary>
/// A uniform DSP node (spec §7.2). <see cref="Process"/> reads <paramref name="frames"/> interleaved <c>f32</c> frames
/// from <c>src</c> and writes the processed result to <c>dst</c> (in-place is legal when the caller passes the same span),
/// returning the frames produced. Copy+process ONLY on this path — no managed alloc, no locks, no blocking (the RT
/// discipline, enforced by <see cref="AudioTripwire"/>).
/// </summary>
public interface IDspStage
{
    /// <summary>Process <paramref name="frames"/> interleaved frames <c>src</c> → <c>dst</c>; returns frames produced.</summary>
    int Process(ReadOnlySpan<float> src, Span<float> dst, int frames, in BlockCtx ctx);
    /// <summary>The stage's added latency in samples (summed into the §7.6 position compensation).</summary>
    int LatencySamples { get; }
    /// <summary>A bypass is a VALUE, not a topology edit (spec §7.2) — a bypassed stage passes src→dst unchanged.</summary>
    bool Bypassed { get; set; }
}

/// <summary>
/// A leaf voice — a decoded (+resampled, +trimmed) source in the fixed mix format (spec §7.2). <c>read(2)</c> short-read
/// semantics: <see cref="Read"/> returns framesWritten (0 = EOF/exhausted, short reads legal). Carries its own
/// <see cref="Gapless"/> trim and <see cref="Loudness"/> (ReplayGain) as SOURCE properties applied at the voice, pre-mix.
/// </summary>
public interface IAudioSource
{
    /// <summary>Fill <paramref name="dst"/> (interleaved f32) with up to <c>dst.Length/Channels</c> frames; returns
    /// framesWritten (0 = exhausted; short reads legal — the mixer always loops).</summary>
    int Read(Span<float> dst, int channels);
    /// <summary>The read cursor in the fixed mix-rate frame domain.</summary>
    long PositionFrames { get; }
    /// <summary>True once the source has produced its last frame (EOF).</summary>
    bool Exhausted { get; }
    /// <summary>The gapless trim info (a source property carried through the chain unchanged; spec §8.3).</summary>
    GaplessInfo Gapless { get; }
    /// <summary>The per-track/album loudness scalar metadata (spec §7.7).</summary>
    ReplayGainInfo Loudness { get; }
}

// ── §7.5 the smoothed param plane ────────────────────────────────────────────────────────────────────────────────────

/// <summary>How a param moves to its target (spec §7.5): <see cref="Immediate"/> = a set (a value, not a ramp);
/// <see cref="Linear"/> for gain/time; <see cref="Multiplicative"/> (geometric) for Hz/dB.</summary>
public enum SmoothKind : byte
{
    /// <summary>Snap to target immediately (a "set" — no ramp).</summary>
    Immediate,
    /// <summary>Linear interpolation (gain, time).</summary>
    Linear,
    /// <summary>Geometric/exponential interpolation (Hz, dB).</summary>
    Multiplicative
}

/// <summary>
/// A smoothed scalar parameter (spec §7.5). The control thread writes <see cref="Target"/> (via <see cref="RampTo"/> /
/// <see cref="Set"/>); the RT/pump advances <see cref="Current"/> toward it per block via <see cref="Advance"/> — linear
/// for gain/time, multiplicative for Hz/dB. Set-vs-ramp is a VALUE (<see cref="SmoothKind"/>), never a branch in a hook.
/// A POD struct — copy/interpolate only, zero-alloc.
/// </summary>
public struct AudioParam
{
    /// <summary>The current (RT-side) value.</summary>
    public float Current;
    /// <summary>The control-side target.</summary>
    public float Target;
    /// <summary>The smoothing law.</summary>
    public SmoothKind Kind;
    /// <summary>Remaining samples until <see cref="Current"/> reaches <see cref="Target"/>.</summary>
    public float RampSamples;

    /// <summary>A param pinned at <paramref name="value"/>.</summary>
    public static AudioParam At(float value) => new() { Current = value, Target = value, Kind = SmoothKind.Immediate, RampSamples = 0f };

    /// <summary>Set the value immediately (no ramp).</summary>
    public void Set(float value) { Current = value; Target = value; RampSamples = 0f; Kind = SmoothKind.Immediate; }

    /// <summary>Ramp toward <paramref name="target"/> over <paramref name="rampSamples"/> using <paramref name="kind"/>.
    /// Idempotent: re-targeting the same value is a no-op (avoids restarting the ramp every block — the zipper trap).</summary>
    public void RampTo(float target, float rampSamples, SmoothKind kind)
    {
        if (target == Target && kind == Kind) return;   // already headed there — don't restart
        Target = target;
        Kind = kind;
        RampSamples = rampSamples <= 0f ? 0f : rampSamples;
        if (RampSamples == 0f) { Current = target; Kind = SmoothKind.Immediate; }
    }

    /// <summary>Advance <see cref="Current"/> toward <see cref="Target"/> by <paramref name="frames"/> samples and return
    /// the value the param HAD at the start of this block (so a stage can lerp start→Current across the block, branch-free
    /// and zipper-free). Deterministic and alloc-free.</summary>
    public float Advance(int frames)
    {
        float start = Current;
        if (RampSamples <= 0f || Current == Target) { Current = Target; RampSamples = 0f; return start; }

        if (frames >= RampSamples)
        {
            Current = Target;
            RampSamples = 0f;
            return start;
        }

        switch (Kind)
        {
            case SmoothKind.Multiplicative when start > 0f && Target > 0f:
            {
                // Geometric step: Current *= (Target/Current)^(frames/RampSamples).
                float ratio = MathF.Pow(Target / start, frames / RampSamples);
                Current = start * ratio;
                break;
            }
            default:
            {
                float step = (Target - start) / RampSamples * frames;
                Current = start + step;
                break;
            }
        }
        RampSamples -= frames;
        return start;
    }
}

/// <summary>
/// The smoothed parameter plane (spec §7.5) — the master-level params the pump advances once per block and stages read.
/// Per-effect params (EQ band gains, etc.) live inside their own stages; this carries the player-master smoothed values
/// plus the shared declick window. A reference type held (never boxed) inside the <see cref="BlockCtx"/> ref struct.
/// </summary>
public sealed class ParamPlane
{
    /// <summary>The master gain (0..1), linearly smoothed — the master <c>Volume</c> signal feeds its target.</summary>
    public AudioParam MasterGain = AudioParam.At(1f);
    /// <summary>The L/R balance (-1..+1), linearly smoothed.</summary>
    public AudioParam Balance = AudioParam.At(0f);
    /// <summary>The default ramp length (samples) a param uses when a control write does not specify one — the
    /// reduced-motion-as-a-value idiom: a short ramp everywhere avoids the zipper without a per-write branch.</summary>
    public float DefaultRampSamples = 512f;
}

// ── §7.4 the graph as a PUBLISHED value ──────────────────────────────────────────────────────────────────────────────

/// <summary>The base of an immutable effect description (spec §7.4). A published <see cref="AudioGraphSpec"/> is compiled
/// to a flat, pre-ordered stage array OFF the RT path; the RT path only reads the compiled value.</summary>
public abstract record EffectSpec
{
    /// <summary>A bypass is a value on the spec — compiled through unchanged.</summary>
    public bool Bypassed { get; init; }
}

/// <summary>A single RBJ biquad band POD (spec §7.4/§7.8): <c>{type, freqHz, q, gainDb}</c>.</summary>
public readonly record struct BiquadBand(BiquadType Type, float FreqHz, float Q, float GainDb);

/// <summary>An EQ effect: a per-channel cascade of RBJ biquad bands (spec §7.4/§7.8).</summary>
public sealed record EqSpec(ImmutableArray<BiquadBand> Bands) : EffectSpec;

/// <summary>A gain effect (linear scalar, dB, or ReplayGain-driven), applied at the voice or master (spec §7.3).</summary>
public sealed record GainSpec(float LinearGain) : EffectSpec;

/// <summary>A channel effect — balance/mono/crossfeed (spec §7.3).</summary>
public sealed record ChannelSpec(float Balance, bool Mono) : EffectSpec;

/// <summary>The TERMINAL brickwall limiter (spec §7.3/§7.7): always present, ~-1.5 dBTP, after any gain/EQ boost.</summary>
public sealed record LimiterSpec(float CeilingDbTp = -1.5f, float ReleaseMs = 50f) : EffectSpec
{
    /// <summary>The default terminal limiter (-1.5 dBTP).</summary>
    public static LimiterSpec Default { get; } = new();
}

/// <summary>A sample-rate-conversion effect — present ONLY when device-rate != mix-rate (normally elided; spec §7.3).</summary>
public sealed record ResampleSpec(int FromRate, int ToRate) : EffectSpec;

/// <summary>
/// The immutable audio graph (spec §7.4): the ordered per-voice chain (applied INSIDE each voice, pre-mix — a fading-out
/// track keeps its own EQ), the ordered master chain (post-mix EQ etc.), and the terminal limiter. Published by atomic
/// swap; NEVER mutated live. Params change on the separate smoothed plane, not by re-publishing.
/// </summary>
public sealed record AudioGraphSpec(
    ImmutableArray<EffectSpec> PerVoiceChain,
    ImmutableArray<EffectSpec> MasterChain,
    LimiterSpec Limiter)
{
    /// <summary>The identity graph: no per-voice effects, no master effects, just the terminal limiter.</summary>
    public static AudioGraphSpec Passthrough { get; } =
        new(ImmutableArray<EffectSpec>.Empty, ImmutableArray<EffectSpec>.Empty, LimiterSpec.Default);
}
