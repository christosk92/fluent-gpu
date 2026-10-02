using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

/// <summary>
/// A per-voice gain envelope (spec §8.2): a precomputed per-sample LUT applied BRANCH-FREE. <see cref="FadeKind.None"/>
/// is a constant-1 envelope (gapless). A fade covers <c>[FadeStartFrame, FadeStartFrame+FadeFrames)</c> in the mixer
/// (device-clock) domain — before/after the window the gain is pinned (in → 0/1, out → 1/0). No drift can accumulate
/// because the frame index is the device-clock domain, never wall-clock.
/// </summary>
public sealed class GainEnvelope
{
    private readonly float[] _lut;   // gain per fade-frame offset (empty for None)
    private long _fadeStartFrame;    // mutable ONLY through StampStart (the render thread, before the envelope is installed)
    /// <summary>Optional atomic commit/cancel decision shared by scheduled incoming/outgoing voices.</summary>
    public AudioTransitionGate? TransitionGate { get; private init; }
    /// <summary>Attach a shared transition decision without recomputing its off-thread lookup table.</summary>
    public GainEnvelope WithTransition(AudioTransitionGate gate)
        => new(Kind, _fadeStartFrame, FadeFrames, _lut) { TransitionGate = gate };

    private GainEnvelope(FadeKind kind, long fadeStartFrame, int fadeFrames, float[] lut)
    {
        Kind = kind;
        _fadeStartFrame = fadeStartFrame;
        FadeFrames = fadeFrames;
        _lut = lut;
    }

    /// <summary>The fade direction.</summary>
    public FadeKind Kind { get; }
    /// <summary>The mixer-domain frame where the fade begins (<see cref="Unstamped"/> until the render thread stamps it).</summary>
    public long FadeStartFrame => _fadeStartFrame;
    /// <summary>The fade length in frames (0 for a gapless / constant envelope).</summary>
    public int FadeFrames { get; }

    /// <summary>The start frame of an envelope whose start the RENDER thread has not stamped yet (see <see cref="StampStart"/>).</summary>
    public const long Unstamped = -1;

    /// <summary>A fade whose start frame is decided on the RENDER thread (seek swap / jump / silence fades): the control side
    /// builds it with <see cref="Unstamped"/> and hands it to the render thread inside a mixer command, which stamps the block
    /// start with <see cref="StampStart"/> before installing it. Allocates its lookup table — build it OFF the render thread, or
    /// derive further shells from one built-once template with <see cref="NewUnstamped"/>.</summary>
    public static GainEnvelope FadeUnstamped(FadeKind kind, int fadeFrames, CrossCurve curve)
        => Fade(kind, Unstamped, fadeFrames, curve);

    /// <summary>A new unstamped shell that SHARES this envelope's lookup table (control side; one small object, no table copy):
    /// every per-command envelope derives from a template built once, so a seek storm never rebuilds a table. A constant
    /// envelope has no start and is returned as is.</summary>
    public GainEnvelope NewUnstamped() => Kind == FadeKind.None ? this : new GainEnvelope(Kind, Unstamped, FadeFrames, _lut);

    /// <summary>RENDER thread only, exactly once, BEFORE the envelope is installed on a voice: fix the start frame to the first
    /// frame of the block the command lands in, so the fade is block-exact however long the command sat in the queue. Alloc-free.
    /// Never stamp an installed or shared envelope (the constant envelope ignores it).</summary>
    public GainEnvelope StampStart(long frame)
    {
        if (Kind != FadeKind.None) _fadeStartFrame = frame;
        return this;
    }

    /// <summary>The fade's lookup table (the shared immutable gain per fade-frame offset) — for tests that pin the curve.</summary>
    internal ReadOnlySpan<float> Lut => _lut;

    /// <summary>The constant-unity (gapless) envelope.</summary>
    public static GainEnvelope Constant { get; } = new(FadeKind.None, 0, 0, Array.Empty<float>());

    /// <summary>Build a fade-in/out envelope over <paramref name="fadeFrames"/> frames starting at
    /// <paramref name="fadeStartFrame"/> (mixer domain) using <paramref name="curve"/>. Precomputes the LUT off the RT path.</summary>
    public static GainEnvelope Fade(FadeKind kind, long fadeStartFrame, int fadeFrames, CrossCurve curve)
    {
        if (kind == FadeKind.None || fadeFrames <= 0) return Constant;
        var lut = new float[fadeFrames];
        for (int i = 0; i < fadeFrames; i++)
        {
            float p = (float)i / fadeFrames;
            lut[i] = kind == FadeKind.In ? CrossfadeCurves.In(curve, p) : CrossfadeCurves.Out(curve, p);
        }
        return new GainEnvelope(kind, fadeStartFrame, fadeFrames, lut);
    }

    /// <summary>The envelope gain at mixer-domain frame <paramref name="mixerFrame"/> — branch-free LUT lookup.</summary>
    public float GainAt(long mixerFrame)
    {
        if (TransitionGate is { } gate && mixerFrame >= gate.StartFrame && !gate.TryCommit())
            return Kind == FadeKind.Out ? 1f : 0f;
        if (Kind == FadeKind.None) return 1f;
        long offset = mixerFrame - FadeStartFrame;
        if (offset < 0) return Kind == FadeKind.In ? 0f : 1f;
        if (offset >= FadeFrames) return Kind == FadeKind.In ? 1f : 0f;
        return _lut[offset];
    }
}

/// <summary>
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
    /// voice's slot, never on a copy.</summary>
    public bool MixInto(Span<float> dst, int frames, in BlockCtx ctx, Span<float> scratch)
    {
        int ch = ctx.Channels;
        long blockStart = ctx.StartFrame;

        if (Held && blockStart >= HoldAtFrame) return false;   // parked: the fade-out that preceded the hold has completed
        int firstActive = (int)Math.Max(0L, StartFrame - blockStart);
        if (firstActive >= frames) return false;   // this voice hasn't started yet in this block
        if (Env.TransitionGate is { } gate && Env.Kind != FadeKind.Out && !gate.TryCommit()) return false;

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

    /// <summary>True when the source is exhausted AND the envelope has faded out — the voice can be retired. A HELD voice is never
    /// finished: its fade-out completing is the hold taking effect, not the voice ending (its ring and byte source stay alive).</summary>
    public readonly bool IsFinished(long mixerFrameAtBlockEnd)
        => !Held && (Env.TransitionGate is { IsCancelled: true } && Env.Kind != FadeKind.Out
            || Src.Exhausted && (Env.Kind != FadeKind.Out || mixerFrameAtBlockEnd >= Env.FadeStartFrame + Env.FadeFrames)
            || Env.Kind == FadeKind.Out && Env.TransitionGate is not { IsCancelled: true }
                && mixerFrameAtBlockEnd >= Env.FadeStartFrame + Env.FadeFrames);
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
            if (ring is null || ring.ProducerDone || voice.IsFinished(ConsumeSeq)) continue;
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

    /// <summary>RENDER thread: publish the drained state at block end (read off the RT thread by the state machine).</summary>
    public void PublishDrained(long mixerFrame) => _drainedPublished = IsDrained(mixerFrame);
}
