using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>Crossfade envelope math (spec §8.2/§8.3). Equal-power (<c>cos/sin</c>) is the default for uncorrelated
/// material; linear for correlated/beatmatched joins. <c>p</c> is the fade progress in <c>[0,1]</c>.</summary>
public static class CrossfadeCurves
{
    /// <summary>The OUTGOING voice's gain at progress <paramref name="p"/> (1 → 0 across the fade).</summary>
    public static float Out(CrossCurve curve, float p)
    {
        p = p < 0f ? 0f : p > 1f ? 1f : p;
        return curve switch
        {
            CrossCurve.Linear => 1f - p,
            _ => MathF.Cos(p * (MathF.PI / 2f)),   // EqualPower / Auto default
        };
    }

    /// <summary>The INCOMING voice's gain at progress <paramref name="p"/> (0 → 1 across the fade).</summary>
    public static float In(CrossCurve curve, float p)
    {
        p = p < 0f ? 0f : p > 1f ? 1f : p;
        return curve switch
        {
            CrossCurve.Linear => p,
            _ => MathF.Sin(p * (MathF.PI / 2f)),   // EqualPower / Auto default
        };
    }
}

/// <summary>The fade direction a <see cref="GainEnvelope"/> applies.</summary>
public enum FadeKind : byte
{
    /// <summary>Constant unity gain (gapless / no fade).</summary>
    None,
    /// <summary>0 → 1 across the fade window (the incoming voice).</summary>
    In,
    /// <summary>1 → 0 across the fade window (the outgoing voice).</summary>
    Out
}

//// <summary>
/// A per-voice gain envelope (spec §8.2) over a shared, immutable gain TABLE sampled at evenly spaced points across the fade
/// window and read with linear interpolation. <see cref="FadeKind.None"/> is a constant-1 envelope (gapless). A fade covers
/// <c>[FadeStartFrame, FadeStartFrame+FadeFrames)</c> in the mixer (device-clock) domain; before the window the gain holds the
/// table's first value and after it the last (an In fade: 0 then 1; an Out fade: 1 then 0). No drift can accumulate because the
/// frame index is the device-clock domain, never wall-clock.
/// <para><b>Tables, not per-frame lookups (#48 E1).</b> The fixed crossfade curves (<see cref="Fade"/>) share ONE table per
/// <see cref="CrossCurve"/> and direction, built once per process, so a 12 s fade no longer allocates 576,000 floats per envelope.
/// A Mix transition hands its own table to <see cref="FromCurve"/> (sampled from Bézier segments by the app, off the render thread);
/// the two envelopes of a join may have different lengths.</para>
/// </summary>
public sealed class GainEnvelope
{
    /// <summary>The point count of the shared <see cref="CrossCurve"/> tables (1,024 intervals). <see cref="FromCurve"/> accepts any
    /// table of at least two points; this is the size the app is expected to sample its curves at.</summary>
    public const int TablePoints = 1025;

    private readonly float[] _table; // gain at evenly spaced positions across the window, first = window start, last = window end
    private long _fadeStartFrame;    // mutable ONLY through StampStart (the render thread, before the envelope is installed)
    /// <summary>Optional atomic commit/cancel decision shared by scheduled incoming/outgoing voices.</summary>
    public AudioTransitionGate? TransitionGate { get; private init; }
    /// <summary>Attach a shared transition decision without recomputing its off-thread table.</summary>
    public GainEnvelope WithTransition(AudioTransitionGate gate)
        => new(Kind, _fadeStartFrame, FadeFrames, _table) { TransitionGate = gate };

    private GainEnvelope(FadeKind kind, long fadeStartFrame, int fadeFrames, float[] table)
    {
        Kind = kind;
        _fadeStartFrame = fadeStartFrame;
        FadeFrames = fadeFrames;
        _table = table;
    }

    /// <summary>The fade direction (drives the transition gate and voice retirement: an Out envelope retires its voice when the
    /// window ends).</summary>
    public FadeKind Kind { get; }
    /// <summary>The mixer-domain frame where the fade begins (<see cref="Unstamped"/> until the render thread stamps it).</summary>
    public long FadeStartFrame => _fadeStartFrame;
    /// <summary>The fade length in frames (0 for a gapless / constant envelope).</summary>
    public int FadeFrames { get; }

    /// <summary>The start frame of an envelope whose start the RENDER thread has not stamped yet (see <see cref="StampStart"/>).</summary>
    public const long Unstamped = -1;

    /// <summary>A fade whose start frame is decided on the RENDER thread (seek swap / jump / silence fades): the control side
    /// builds it with <see cref="Unstamped"/> and hands it to the render thread inside a mixer command, which stamps the block
    /// start with <see cref="StampStart"/> before installing it. Derive further shells from one template with <see cref="NewUnstamped"/>.</summary>
    public static GainEnvelope FadeUnstamped(FadeKind kind, int fadeFrames, CrossCurve curve)
        => Fade(kind, Unstamped, fadeFrames, curve);

    /// <summary>A new unstamped shell that SHARES this envelope's table (control side; one small object, no table copy):
    /// every per-command envelope derives from a template built once, so a seek storm never rebuilds a table. A constant
    /// envelope has no start and is returned as is.</summary>
    public GainEnvelope NewUnstamped() => Kind == FadeKind.None ? this : new GainEnvelope(Kind, Unstamped, FadeFrames, _table);

    /// <summary>RENDER thread only, exactly once, BEFORE the envelope is installed on a voice: fix the start frame to the first
    /// frame of the block the command lands in, so the fade is block-exact however long the command sat in the queue. Alloc-free.
    /// Never stamp an installed or shared envelope (the constant envelope ignores it).</summary>
    public GainEnvelope StampStart(long frame)
    {
        if (Kind != FadeKind.None) _fadeStartFrame = frame;
        return this;
    }

    /// <summary>The constant-unity (gapless) envelope.</summary>
    public static GainEnvelope Constant { get; } = new(FadeKind.None, 0, 0, Array.Empty<float>());

    // The shared CrossCurve tables: [EqualPower In, EqualPower Out, Linear In, Linear Out]. Built once, lazily, never mutated.
    private static readonly float[]?[] s_curveTables = new float[]?[4];

    private static float[] CurveTable(FadeKind kind, CrossCurve curve)
    {
        int slot = (curve == CrossCurve.Linear ? 2 : 0) + (kind == FadeKind.Out ? 1 : 0);
        if (Volatile.Read(ref s_curveTables[slot]) is { } built) return built;
        const int last = TablePoints - 1;
        var table = new float[TablePoints];
        for (int i = 0; i < TablePoints; i++)
        {
            float p = (float)i / last;
            table[i] = kind == FadeKind.In ? CrossfadeCurves.In(curve, p) : CrossfadeCurves.Out(curve, p);
        }
        // The value held after the window is exact (an equal-power Out would end at cos(pi/2) = -4e-8 in float otherwise).
        table[last] = kind == FadeKind.In ? 1f : 0f;
        return Interlocked.CompareExchange(ref s_curveTables[slot], table, null) ?? table;
    }

    /// <summary>Build a fade-in/out envelope over <paramref name="fadeFrames"/> frames starting at
    /// <paramref name="fadeStartFrame"/> (mixer domain) using <paramref name="curve"/>. Shares the curve's process-wide table:
    /// allocates only the envelope object.</summary>
    public static GainEnvelope Fade(FadeKind kind, long fadeStartFrame, int fadeFrames, CrossCurve curve)
    {
        if (kind == FadeKind.None || fadeFrames <= 0) return Constant;
        return new GainEnvelope(kind, fadeStartFrame, fadeFrames, CurveTable(kind, curve));
    }

    /// <summary>A curve envelope (#48 E1): the gain across <c>[startFrame, startFrame+frames)</c> is <paramref name="table"/>
    /// (at least two points, evenly spaced over the window, normally <see cref="TablePoints"/>) read with linear interpolation;
    /// before the window it holds <c>table[0]</c>, after it <c>table[^1]</c>. <paramref name="kind"/> keeps the transition-gate and
    /// retirement semantics of <see cref="Fade"/> (In = the incoming voice, Out = the outgoing voice, retired when the window ends).
    /// The table is taken as is, not copied: build it off the render thread and never mutate it afterwards.</summary>
    public static GainEnvelope FromCurve(FadeKind kind, long startFrame, int frames, float[] table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Length < 2) throw new ArgumentException("A curve table needs at least two points.", nameof(table));
        if (kind == FadeKind.None) throw new ArgumentException("A curve envelope fades In or Out.", nameof(kind));
        return new GainEnvelope(kind, startFrame, Math.Max(1, frames), table);
    }

    /// <summary>The envelope gain at mixer-domain frame <paramref name="mixerFrame"/>: the table read with linear interpolation.
    /// Alloc-free.</summary>
    public float GainAt(long mixerFrame)
    {
        if (TransitionGate is { } gate && mixerFrame >= gate.StartFrame && !gate.TryCommit())
            return Kind == FadeKind.Out ? 1f : 0f;
        if (Kind == FadeKind.None) return 1f;
        var table = _table;
        long offset = mixerFrame - FadeStartFrame;
        if (offset <= 0) return table[0];
        if (offset >= FadeFrames) return table[^1];
        double x = (double)offset * (table.Length - 1) / FadeFrames;
        int i = (int)x;
        float t = (float)(x - i);
        float a = table[i];
        return a + (table[i + 1] - a) * t;
    }
}

// <summary>
/// One mixer voice (spec §8.2): a leaf <see cref="IAudioSource"/> with its OWN pre-mix DSP chain (EQ + gain — a fading-out
/// track keeps its own EQ), its ReplayGain scalar baked PER-SOURCE before the mix (critical when crossfading two tracks
/// at different gains; §7.7), a start frame in the mixer timeline, and a gain envelope for the crossfade. A struct — no
/// per-block alloc; its source's cursor advances internally.
/// </summary>
public struct MixVoice
{
    /// <summary>An optional caller-assigned voice identity (0 = unassigned). The <see cref="VoiceScheduler"/> uses it to
    /// retarget the OUTGOING voice's envelope when a crossfade commits, without depending on the (shifting) list index.</summary>
    public long Id;
    /// <summary>The leaf source (already decoded/resampled/trimmed into the fixed mix format).</summary>
    public IAudioSource Src;
    /// <summary>The crossfade envelope (constant for gapless).</summary>
    public GainEnvelope Env;
    /// <summary>The mixer-domain frame this voice starts sounding at.</summary>
    public long StartFrame;
    /// <summary>The ReplayGain (× track/album) linear scalar, baked per-source PRE-mix.</summary>
    public float ReplayGainScalar;
    /// <summary>The per-voice DSP chain (EQ/gain), applied in-place pre-mix; may be null.</summary>
    public IDspStage[]? Chain;
    /// <summary>A HOLD: from <see cref="HoldAtFrame"/> on the voice is parked — never read, never retired, never waited for — until
    /// a new envelope is installed on it (<see cref="CrossfadeMixer.TrySetVoiceEnvelope"/> clears the hold). The hold's own fade-out
    /// envelope (installed with it) carries the voice to silence BEFORE <see cref="HoldAtFrame"/>, so parking it is inaudible. The
    /// seek stale-fade and the scrub hold both use it: the voice keeps its ring, its decoder, its byte source.</summary>
    public bool Held;
    /// <summary>The mixer-domain frame the hold takes effect at (the end of the hold's fade-out). Meaningful only while <see cref="Held"/>.</summary>
    public long HoldAtFrame;
    /// <summary>The 5 ms blend a seek jump inside the ring leaves in the voice's slot (the state is written while mixing, so it lives
    /// in the slot the mixer iterates by reference, never in a copy). Empty when no jump blend is pending.</summary>
    public JumpBlend Blend;

    /// <summary>Pull this voice's active frames for the block, apply its DSP + ReplayGain + envelope, and ADD into
    /// <paramref name="dst"/>. <paramref name="scratch"/> is the shared per-voice work buffer (mixer-owned, reused —
    /// zero-alloc). Returns true if the voice produced any samples this block. Mutates <see cref="Blend"/>: call it on the
    /// voice's slot, never on a copy. A voice whose automation runs effects (#48 E7) takes <see cref="MixWithEffects"/>.</summary>
    public bool MixInto(Span<float> dst, int frames, in BlockCtx ctx, Span<float> scratch)
    {
        int ch = ctx.Channels;
        long blockStart = ctx.StartFrame;
        var effects = Effects;

        if (Held && blockStart >= HoldAtFrame) { effects?.SilenceTails(); return false; }   // parked: the fade-out that preceded the hold has completed
        int firstActive = (int)Math.Max(0L, StartFrame - blockStart);
        if (firstActive >= frames) return false;   // this voice hasn't started yet in this block
        if (Env.TransitionGate is { } gate && Env.Kind != FadeKind.Out && !gate.TryCommit()) return false;
        if (effects is not null) return MixWithEffects(dst, frames, firstActive, in ctx, scratch, effects);

        int want = frames - firstActive;
        var work = scratch[..(want * ch)];
        int got = 0;
        while (got < want)
        {
            int count = Src.Read(work[(got * ch)..], ch);
            if (count <= 0) break;
            got += count;
            if (Src.Exhausted) break;
        }
        if (got <= 0) return false;

        // A seek jump inside the ring: the first `Blend.Frames` fresh frames are an equal-power cross with the audio the old
        // position would have played next. Applied BEFORE the voice chain so the EQ/gain state sees one continuous signal.
        if (Blend.Remaining > 0) Blend.Apply(work[..(got * ch)], got);

        // Per-voice DSP chain (EQ + gain) in place, pre-mix.
        if (Chain is { Length: > 0 } chain)
        {
            var subCtx = new BlockCtx(blockStart + firstActive, ctx.MixRate, ch, ctx.Params);
            var stageSpan = work[..(got * ch)];
            for (int s = 0; s < chain.Length; s++) chain[s].Process(stageSpan, stageSpan, got, subCtx);
        }

        float rg = ReplayGainScalar <= 0f ? 1f : ReplayGainScalar;
        for (int f = 0; f < got; f++)
        {
            long mixerFrame = blockStart + firstActive + f;
            float g = Env.GainAt(mixerFrame) * rg;
            int sb = f * ch;
            int db = (firstActive + f) * ch;
            for (int c = 0; c < ch; c++) dst[db + c] += work[sb + c] * g;
        }
        return true;
    }

    /// <summary>#48 E7: the chain's automation stage when it runs effects after the envelope (armed, or a tail still ringing);
    /// null otherwise — then the voice mixes exactly as it always has.</summary>
    private readonly VoiceAutomationStage? Effects
        => Chain is { Length: > 0 } chain && chain[^1] is VoiceAutomationStage { HasPost: true } stage ? stage : null;

    /// <summary>
    /// #48 E7: <see cref="MixInto"/> for a voice with effects. The dry part is read, run through the chain and scaled by the envelope
    /// and ReplayGain as usual; the effects then run on that ENVELOPED signal (fed through their sends), so their tails outlive the
    /// envelope: when the dry part is over (<see cref="DryFinished"/>: the source is exhausted, or an Out envelope's window has
    /// ended) nothing more is read from the source and the effects play on over silence until they have rung out. A HELD voice runs
    /// its effects before the envelope instead, so the hold's fade-out fades the tails with it (and parking it silences them).
    /// Alloc-free.
    /// </summary>
    private bool MixWithEffects(Span<float> dst, int frames, int firstActive, in BlockCtx ctx, Span<float> scratch, VoiceAutomationStage effects)
    {
        int ch = ctx.Channels;
        long first = ctx.StartFrame + firstActive;
        int want = frames - firstActive;
        var work = scratch[..(want * ch)];
        int got = 0;
        if (!DryFinished(ctx.StartFrame))
        {
            while (got < want)
            {
                int count = Src.Read(work[(got * ch)..], ch);
                if (count <= 0) break;
                got += count;
                if (Src.Exhausted) break;
            }
            if (got > 0)
            {
                if (Blend.Remaining > 0) Blend.Apply(work[..(got * ch)], got);
                if (Chain is { Length: > 0 } chain)
                {
                    var subCtx = new BlockCtx(first, ctx.MixRate, ch, ctx.Params);
                    var stageSpan = work[..(got * ch)];
                    for (int s = 0; s < chain.Length; s++) chain[s].Process(stageSpan, stageSpan, got, subCtx);
                }
            }
        }
        if (got <= 0 && !effects.TailRinging) return false;
        work[(got * ch)..].Clear();   // no dry audio past the source's end: the tails play on over silence
        float rg = ReplayGainScalar <= 0f ? 1f : ReplayGainScalar;
        if (!Held) ApplyEnvelope(work, got, ch, first, rg);
        effects.ProcessPost(work, want, ch, first);
        if (Held) ApplyEnvelope(work, want, ch, first, rg);
        var into = dst[(firstActive * ch)..];
        for (int i = 0; i < work.Length; i++) into[i] += work[i];
        return true;
    }

    private readonly void ApplyEnvelope(Span<float> work, int frames, int ch, long first, float rg)
    {
        for (int f = 0; f < frames; f++)
        {
            float g = Env.GainAt(first + f) * rg;
            int b = f * ch;
            for (int c = 0; c < ch; c++) work[b + c] *= g;
        }
    }

    /// <summary>True when the voice's OWN audio is over: the source is exhausted and the envelope has faded out, or an Out
    /// envelope's window has ended, or its transition was cancelled before it sounded. Nothing more is read from its source. A
    /// HELD voice is never finished: its fade-out completing is the hold taking effect, not the voice ending (its ring and byte
    /// source stay alive). Without effects this is <see cref="IsFinished"/>.</summary>
    public readonly bool DryFinished(long mixerFrame)
        => !Held && (Env.TransitionGate is { IsCancelled: true } && Env.Kind != FadeKind.Out
            || Src.Exhausted && (Env.Kind != FadeKind.Out || mixerFrame >= Env.FadeStartFrame + Env.FadeFrames)
            || Env.Kind == FadeKind.Out && Env.TransitionGate is not { IsCancelled: true }
                && mixerFrame >= Env.FadeStartFrame + Env.FadeFrames);

    /// <summary>True when the voice can be retired: its own audio is over (<see cref="DryFinished"/>) and no effect tail is still
    /// ringing (#48 E7; a voice cancelled before it sounded is cut, tails included).</summary>
    public readonly bool IsFinished(long mixerFrameAtBlockEnd)
        => DryFinished(mixerFrameAtBlockEnd)
            && (Env.TransitionGate is { IsCancelled: true } && Env.Kind != FadeKind.Out
                || Chain is not { Length: > 0 } chain || chain[^1] is not VoiceAutomationStage { TailRinging: true });
}

/// <summary>
/// The 5 ms equal-power blend a seek jump inside the decode ring leaves in its voice's slot (seek design B). The tail is the audio
/// the voice would have played next at the OLD position (read from the voice itself, so a stretched voice's own lookahead and
/// rate are honoured); the fresh audio is what the voice reads at the NEW one. Frame <c>i</c> of the blend is
/// <c>fresh·sin(p·π/2) + tail·cos(p·π/2)</c>, <c>p = i/Frames</c> — power-complementary (sin² + cos² = 1) and continuous with the
/// old signal at <c>i = 0</c>. The tail buffer is session-owned and preallocated: starting and applying a blend never allocates.
/// </summary>
public struct JumpBlend
{
    /// <summary>The interleaved tail (≥ <see cref="Frames"/> × <see cref="Channels"/> floats); null when no blend is pending.</summary>
    public float[]? Tail;
    /// <summary>The blend length in frames.</summary>
    public int Frames;
    /// <summary>Frames already blended.</summary>
    public int Done;
    /// <summary>The channel count of <see cref="Tail"/>.</summary>
    public int Channels;

    /// <summary>Frames still to blend (0 when empty).</summary>
    public readonly int Remaining => Tail is null ? 0 : Frames - Done;

    /// <summary>Begin a blend over <paramref name="frames"/> frames of <paramref name="tail"/>.</summary>
    public static JumpBlend Start(float[] tail, int frames, int channels)
        => new() { Tail = tail, Frames = frames, Done = 0, Channels = channels };

    /// <summary>Blend the next <paramref name="frames"/> frames of the voice's fresh audio in place (at most <see cref="Remaining"/>
    /// are touched). Alloc-free; the render thread only.</summary>
    public void Apply(Span<float> fresh, int frames)
    {
        if (Tail is not { } tail) return;
        int n = Math.Min(frames, Frames - Done);
        int ch = Channels;
        for (int f = 0; f < n; f++)
        {
            float p = (float)(Done + f) / Frames;
            float gainIn = CrossfadeCurves.In(CrossCurve.EqualPower, p);
            float gainOut = CrossfadeCurves.Out(CrossCurve.EqualPower, p);
            int fb = f * ch, tb = (Done + f) * ch;
            for (int c = 0; c < ch; c++) fresh[fb + c] = fresh[fb + c] * gainIn + tail[tb + c] * gainOut;
        }
        Done += n;
        if (Done >= Frames) this = default;
    }
}

/// <summary>
/// THE mixing primitive (spec §8.2/§9): N voices summed then mastered. Gapless = butt-joined trimmed PCM with a
/// constant envelope (overlap 0); crossfade = two live voices overlapping N frames through per-sample gain envelopes.
/// Per-voice EQ + gain + ReplayGain happen INSIDE each voice (pre-mix). <see cref="ConsumeSeq"/> counts frames consumed —
/// it drives the §7.4 graph quarantine and the §7.6 position. Voices are a pre-sized list (no per-block alloc); the
/// per-voice scratch is mixer-owned and reused.
/// </summary>
public sealed class CrossfadeMixer
{
    // Voices are RENDER-thread-owned (control mutations arrive via the session's mixer-command SPSC). Capacity 8 so an
    // RT-side Add never grows the backing store mid-render (the zero-alloc gate).
    private readonly List<MixVoice> _voices = new(8);
    private readonly float[] _scratch;   // sized MaxBlock*channels, reused every block
    private readonly int _channels;
    private readonly int _maxBlock;
    private volatile bool _drainedPublished;

    // Voices retired during the LAST Render() call (RT thread writes; same-thread read by RenderBlock right after). The
    // ids are handed to the worker for off-RT ring disposal — the RT thread never frees the ring itself (spec §7.9).
    private readonly long[] _retired = new long[8];
    private readonly IAudioSource?[] _retiredSources = new IAudioSource?[8];
    private int _retiredCount;

    /// <summary>Frames consumed out of the mixer (the device-clock domain; drives quarantine + position).</summary>
    public long ConsumeSeq;

    /// <summary>Create a mixer for <paramref name="channels"/> channels with a maximum pull block of
    /// <paramref name="maxBlock"/> frames.</summary>
    public CrossfadeMixer(int channels, int maxBlock)
    {
        _channels = Math.Max(1, channels);
        _maxBlock = Math.Max(1, maxBlock);
        _scratch = new float[_maxBlock * _channels];
    }

    /// <summary>The live voice count.</summary>
    public int VoiceCount => _voices.Count;
    /// <summary>The max pull block (frames).</summary>
    public int MaxBlock => _maxBlock;

    /// <summary>PCM preflight: stop at the first unfilled active ring without advancing either voice or envelope.</summary>
    public int ReadableFrames(int requested, out RingAudioSource? waitingFor)
    {
        waitingFor = null;
        int readable = requested;
        foreach (var voice in CollectionsMarshal.AsSpan(_voices))
        {
            if (voice.Env.TransitionGate is { IsCancelled: true } && voice.Env.Kind != FadeKind.Out) continue;
            if (voice.Held && ConsumeSeq >= voice.HoldAtFrame) continue;   // parked: nothing is read from it, so it can never stall the mixer
            // A confirmed tail may finish inside this block while another voice continues across the join.
            // Only an unfinished producer can make the content timeline wait for unavailable PCM.
            var stretched = voice.Src as WsolaAudioSource;
            var ring = stretched?.Ring ?? voice.Src as RingAudioSource;
            // DryFinished, not IsFinished: a voice whose effects ring out after its own audio reads nothing more (#48 E7).
            if (ring is null || ring.ProducerDone || voice.DryFinished(ConsumeSeq)) continue;
            long offset = Math.Max(0, voice.StartFrame - ConsumeSeq);
            if (offset >= readable) continue;
            int available = stretched?.ReadableFrames(requested) ?? ring.BufferedFrames;
            long safe = offset + available;
            if (safe < readable)
            {
                readable = (int)safe;
                if (!ring.ProducerDone) waitingFor = ring;
            }
        }
        return readable;
    }

    /// <summary>All currently audible rings have a recovery cushion or confirmed complete short content.</summary>
    public bool PcmReady(int thresholdFrames)
    {
        foreach (var voice in CollectionsMarshal.AsSpan(_voices))
        {
            if (voice.StartFrame > ConsumeSeq || voice.Env.TransitionGate is { IsCancelled: true } && voice.Env.Kind != FadeKind.Out) continue;
            if (voice.Held && ConsumeSeq >= voice.HoldAtFrame) continue;   // parked: not audible, so it needs no cushion
            if (voice.DryFinished(ConsumeSeq)) continue;   // only an effect tail left (#48 E7): its source is not read again
            var stretched = voice.Src as WsolaAudioSource;
            var ring = stretched?.Ring ?? voice.Src as RingAudioSource;
            if (ring is null) continue;
            // H-2: the requested cushion applies to a stretched (WSOLA) voice too — the former one-hop clamp let a starved
            // podcast voice resume on 20 ms of audio and starve again at once (the stutter loop). The only bound left is what a
            // FULL ring can supply at the current rate, so a cushion that has grown past it can never wedge the gate.
            int cushion = stretched is null ? thresholdFrames : Math.Min(thresholdFrames, stretched.ReachableFrames(ring.TargetFrames));
            if (ring.HasPendingFlush || !ring.ProducerDone &&
                (stretched?.ReadableFrames(cushion) ?? ring.BufferedFrames) < Math.Min(cushion, ring.TargetFrames)) return false;
        }
        return true;
    }

    /// <summary>A mutable view over the live voices (RENDER thread only — control mutations arrive via the session's
    /// mixer-command SPSC; on the single-thread pull path control IS the render thread). The <see cref="VoiceScheduler"/>
    /// uses it to retarget the outgoing voice's envelope at the crossfade commit.</summary>
    public Span<MixVoice> VoicesSpan => CollectionsMarshal.AsSpan(_voices);

    /// <summary>Retarget the envelope of the voice with <paramref name="id"/> (RENDER thread — via the session command SPSC,
    /// or inline on the single-thread pull path) — the outgoing-voice fade-out a crossfade commit installs. Installing an
    /// envelope also ENDS a hold on the voice (<see cref="MixVoice.Held"/>): a parked voice is restored by giving it a fade-in.
    /// Returns false when no such voice is live.</summary>
    public bool TrySetVoiceEnvelope(long id, GainEnvelope env)
    {
        var span = CollectionsMarshal.AsSpan(_voices);
        for (int i = 0; i < span.Length; i++)
            if (span[i].Id == id)
            {
                span[i].Env = env;
                span[i].Held = false;   // installing an envelope ENDS a hold: the voice is mixed again, under the new envelope
                return true;
            }
        return false;
    }

    /// <summary>The slot of the voice with <paramref name="id"/>, BY REFERENCE (RENDER thread only): writes persist, unlike the
    /// struct copies <c>foreach</c> hands out. Valid until the next <see cref="AddVoice"/>/<see cref="RemoveVoice"/>/<see cref="Clear"/>/<see cref="Render"/>.
    /// Returns a null reference when no such voice is live — test it with <see cref="Unsafe.IsNullRef{T}"/>.</summary>
    public ref MixVoice VoiceRef(long id)
    {
        var span = CollectionsMarshal.AsSpan(_voices);
        for (int i = 0; i < span.Length; i++)
            if (span[i].Id == id) return ref span[i];
        return ref Unsafe.NullRef<MixVoice>();
    }

    /// <summary>True when a voice with <paramref name="id"/> is currently live.</summary>
    public bool HasVoice(long id)
    {
        var span = CollectionsMarshal.AsSpan(_voices);
        for (int i = 0; i < span.Length; i++) if (span[i].Id == id) return true;
        return false;
    }

    /// <summary>Add a voice (RENDER thread — via the session command SPSC, or inline on the single-thread pull path). The
    /// voice's <c>StartFrame</c> is the mixer-domain frame it begins at.</summary>
    public void AddVoice(in MixVoice voice) => _voices.Add(voice);

    /// <summary>Remove all voices (a hard stop / source change — RENDER thread, via the session command SPSC or inline).</summary>
    public void Clear() => _voices.Clear();

    /// <summary>Render-thread removal; the session acknowledges source retirement.</summary>
    public bool RemoveVoice(long id)
    {
        for (int i = 0; i < _voices.Count; i++)
            if (_voices[i].Id == id) { _voices.RemoveAt(i); return true; }
        return false;
    }

    /// <summary>Sum every live voice into <paramref name="dst"/> for <paramref name="frames"/> frames (≤ MaxBlock),
    /// retire finished voices, and advance <see cref="ConsumeSeq"/>. Zero-alloc.</summary>
    public int Render(Span<float> dst, int frames, in BlockCtx ctx)
    {
        if (frames > _maxBlock) frames = _maxBlock;
        Array.Clear(_retiredSources, 0, _retiredCount);
        _retiredCount = 0;
        int n = frames * ctx.Channels;
        dst[..n].Clear();

        var scratch = _scratch.AsSpan();
        // BY REFERENCE: MixInto advances per-voice state (the jump blend) that a struct copy would silently lose.
        var voices = CollectionsMarshal.AsSpan(_voices);
        for (int i = 0; i < voices.Length; i++)
            voices[i].MixInto(dst, frames, in ctx, scratch);

        ConsumeSeq += frames;
        long blockEnd = ctx.StartFrame + frames;

        // Retire finished voices (source exhausted + fade complete). RemoveAt is alloc-free. Record the retired ids so
        // RenderBlock can hand their rings to the worker for off-RT disposal (the RT thread must never free the ring).
        for (int i = _voices.Count - 1; i >= 0; i--)
            if (_voices[i].IsFinished(blockEnd))
            {
                if (_retiredCount < _retired.Length)
                {
                    _retired[_retiredCount] = _voices[i].Id;
                    _retiredSources[_retiredCount++] = _voices[i].Src;
                }
                _voices.RemoveAt(i);
            }

        return frames;
    }

    /// <summary>The ids of voices retired during the most recent <see cref="Render"/> call (same-thread read only —
    /// consumed by <c>RenderBlock</c> immediately after <see cref="Render"/> to hand each retired voice's ring to the
    /// worker for off-RT disposal). Empty until the next <see cref="Render"/> resets it.</summary>
    public ReadOnlySpan<long> RetiredThisBlock => _retired.AsSpan(0, _retiredCount);
    /// <summary>Exact source identities retired by the last render, released off the DSP path.</summary>
    public ReadOnlySpan<IAudioSource?> RetiredSourcesThisBlock => _retiredSources.AsSpan(0, _retiredCount);

    /// <summary>True when every voice is finished (source exhausted + faded) — the mixer has no more audio.</summary>
    public bool IsDrained(long mixerFrame)
    {
        for (int i = 0; i < _voices.Count; i++)
            if (!_voices[i].IsFinished(mixerFrame)) return false;
        return true;
    }

    /// <summary>The RT-published "all voices finished" flag — the ONLY drained signal the control thread may read on the RT
    /// path (spec §12; the control thread never touches <see cref="_voices"/>).</summary>
    public bool DrainedPublished => _drainedPublished;

    /// <summary>RENDER thread: publish the drained state at block end (read off the RT thread by the state machine).
    /// <paramref name="masterFlushed"/> false holds it at "not drained" while the master chain downstream of the mixer still holds
    /// the last voice's final frames (the terminal limiter's lookahead): the session renders silence through it first.</summary>
    public void PublishDrained(long mixerFrame, bool masterFlushed = true) => _drainedPublished = masterFlushed && IsDrained(mixerFrame);
}
