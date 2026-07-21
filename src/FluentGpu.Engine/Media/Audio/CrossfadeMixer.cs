using System;
using System.Collections.Generic;
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

    private GainEnvelope(FadeKind kind, long fadeStartFrame, int fadeFrames, float[] lut)
    {
        Kind = kind;
        FadeStartFrame = fadeStartFrame;
        FadeFrames = fadeFrames;
        _lut = lut;
    }

    /// <summary>The fade direction.</summary>
    public FadeKind Kind { get; }
    /// <summary>The mixer-domain frame where the fade begins.</summary>
    public long FadeStartFrame { get; }
    /// <summary>The fade length in frames (0 for a gapless / constant envelope).</summary>
    public int FadeFrames { get; }

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

    /// <summary>Pull this voice's active frames for the block, apply its DSP + ReplayGain + envelope, and ADD into
    /// <paramref name="dst"/>. <paramref name="scratch"/> is the shared per-voice work buffer (mixer-owned, reused —
    /// zero-alloc). Returns true if the voice produced any samples this block.</summary>
    public bool MixInto(Span<float> dst, int frames, in BlockCtx ctx, Span<float> scratch)
    {
        int ch = ctx.Channels;
        long blockStart = ctx.StartFrame;

        int firstActive = (int)Math.Max(0L, StartFrame - blockStart);
        if (firstActive >= frames) return false;   // this voice hasn't started yet in this block

        int want = frames - firstActive;
        var work = scratch[..(want * ch)];
        int got = Src.Read(work, ch);
        if (got <= 0) return false;

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

    /// <summary>True when the source is exhausted AND the envelope has faded out — the voice can be retired.</summary>
    public readonly bool IsFinished(long mixerFrameAtBlockEnd)
        => Src.Exhausted && (Env.Kind != FadeKind.Out || mixerFrameAtBlockEnd >= Env.FadeStartFrame + Env.FadeFrames);
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

    /// <summary>A mutable view over the live voices (RENDER thread only — control mutations arrive via the session's
    /// mixer-command SPSC; on the single-thread pull path control IS the render thread). The <see cref="VoiceScheduler"/>
    /// uses it to retarget the outgoing voice's envelope at the crossfade commit.</summary>
    public Span<MixVoice> VoicesSpan => CollectionsMarshal.AsSpan(_voices);

    /// <summary>Retarget the envelope of the voice with <paramref name="id"/> (RENDER thread — via the session command SPSC,
    /// or inline on the single-thread pull path) — the outgoing-voice fade-out a crossfade commit installs. Returns false
    /// when no such voice is live.</summary>
    public bool TrySetVoiceEnvelope(long id, GainEnvelope env)
    {
        var span = CollectionsMarshal.AsSpan(_voices);
        for (int i = 0; i < span.Length; i++)
            if (span[i].Id == id) { span[i].Env = env; return true; }
        return false;
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

    /// <summary>Sum every live voice into <paramref name="dst"/> for <paramref name="frames"/> frames (≤ MaxBlock),
    /// retire finished voices, and advance <see cref="ConsumeSeq"/>. Zero-alloc.</summary>
    public int Render(Span<float> dst, int frames, in BlockCtx ctx)
    {
        if (frames > _maxBlock) frames = _maxBlock;
        _retiredCount = 0;
        int n = frames * ctx.Channels;
        dst[..n].Clear();

        var scratch = _scratch.AsSpan();
        for (int i = 0; i < _voices.Count; i++)
        {
            var v = _voices[i];
            v.MixInto(dst, frames, in ctx, scratch);
        }

        ConsumeSeq += frames;
        long blockEnd = ctx.StartFrame + frames;

        // Retire finished voices (source exhausted + fade complete). RemoveAt is alloc-free. Record the retired ids so
        // RenderBlock can hand their rings to the worker for off-RT disposal (the RT thread must never free the ring).
        for (int i = _voices.Count - 1; i >= 0; i--)
            if (_voices[i].IsFinished(blockEnd))
            {
                if (_voices[i].Id != 0 && _retiredCount < _retired.Length) _retired[_retiredCount++] = _voices[i].Id;
                _voices.RemoveAt(i);
            }

        return frames;
    }

    /// <summary>The ids of voices retired during the most recent <see cref="Render"/> call (same-thread read only —
    /// consumed by <c>RenderBlock</c> immediately after <see cref="Render"/> to hand each retired voice's ring to the
    /// worker for off-RT disposal). Empty until the next <see cref="Render"/> resets it.</summary>
    public ReadOnlySpan<long> RetiredThisBlock => _retired.AsSpan(0, _retiredCount);

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
