using System;
using System.Collections.Generic;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The engine's mixer without a device: voices (a source, its gain envelope, its automation, its rate plan) summed by the real
// CrossfadeMixer through the real per-voice chain (AudioGraphHost.BuildVoiceChain: gain, EQ, VoiceAutomationStage) block by block,
// as the render thread does, into a buffer. What a live session would play, rendered ahead: a preview that must be compared with a
// variant of itself (the same stretch with one lane bypassed), a bench, a bounce. Control thread or a worker; not real-time.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Renders voices OFFLINE through the engine's own mixer and voice chain (see the file header). Add the voices, then
/// <see cref="Render(int)"/> (or <see cref="Render(Span{float}, int)"/> repeatedly). A voice with a rate plan is read through a
/// <see cref="WsolaAudioSource"/> stepped on the mixer frame, as the session does; an automation is armed on the voice's
/// <see cref="VoiceAutomationStage"/> before its first block. One instance renders one timeline (frame 0 = the first rendered frame).
/// Not thread-safe; allocates (it is not the real-time path).
/// </summary>
public sealed class OfflineMixer
{
    private readonly CrossfadeMixer _mixer;
    private readonly AudioGraphHost _host;
    private readonly ParamPlane _plane = new();
    private readonly float[] _block;
    private long _nextId = 1;

    /// <summary>The mix rate (frames per second) every voice, envelope and automation is in.</summary>
    public int SampleRate { get; }
    /// <summary>The interleaved channel count of the output.</summary>
    public int Channels { get; }
    /// <summary>The block the mixer renders at a time (the render thread's).</summary>
    public int BlockFrames { get; }
    /// <summary>The frames rendered so far: the mixer frame the next <see cref="Render(Span{float}, int)"/> starts at.</summary>
    public long Frame => _mixer.ConsumeSeq;
    /// <summary>The voices still in the mixer (a voice retires when its source and its envelope are done and its effect tails rang out).</summary>
    public int Voices => _mixer.VoiceCount;

    /// <summary>A mixer at <paramref name="sampleRate"/> with <paramref name="channels"/> channels, rendering <paramref name="blockFrames"/>
    /// frames a block.</summary>
    public OfflineMixer(int sampleRate, int channels = 2, int blockFrames = 480)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
        Channels = Math.Max(1, channels);
        BlockFrames = Math.Max(16, blockFrames);
        _mixer = new CrossfadeMixer(Channels, BlockFrames);
        _host = new AudioGraphHost(Channels, sampleRate);
        _block = new float[BlockFrames * Channels];
    }

    /// <summary>Add a voice: <paramref name="source"/> (at the mix rate, <see cref="Channels"/> channels) from mixer frame
    /// <paramref name="startFrame"/>, scaled by <paramref name="envelope"/> (null: unity), with <paramref name="automation"/> armed on
    /// its chain (built at this mixer's rate by <see cref="VoiceAutomation.Build(VoiceAutomationSpec)"/>; one automation per voice) and
    /// read at <paramref name="rate"/> (a <see cref="VoiceRatePlan"/> in mixer frames; null: as recorded). Returns the voice's id.</summary>
    public long AddVoice(IAudioSource source, GainEnvelope? envelope = null, long startFrame = 0, VoiceAutomation? automation = null,
        VoiceRatePlan? rate = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        IAudioSource src = rate is null ? source : new WsolaAudioSource(source, SampleRate, Channels, rate);
        IDspStage[] chain = _host.Live.BuildVoiceChain();
        if (automation is not null && chain.Length > 0 && chain[^1] is VoiceAutomationStage stage) stage.Arm(automation);
        long id = _nextId++;
        _mixer.AddVoice(new MixVoice
        {
            Id = id, Src = src, Env = envelope ?? GainEnvelope.Constant, StartFrame = Math.Max(0, startFrame), ReplayGainScalar = 1f, Chain = chain,
        });
        return id;
    }

    /// <summary>Render the next <paramref name="frames"/> frames into <paramref name="dst"/> (interleaved, at least
    /// <c>frames × Channels</c> long), block by block.</summary>
    public void Render(Span<float> dst, int frames)
    {
        if (dst.Length < frames * Channels) throw new ArgumentException("The buffer is shorter than the frames asked for.", nameof(dst));
        for (int done = 0; done < frames;)
        {
            int n = Math.Min(BlockFrames, frames - done);
            long blockStart = _mixer.ConsumeSeq;
            // A voice's own rate plan steps on the mixer frame its next output frame is heard at (PcmAudioSession does the same).
            foreach (ref readonly var voice in _mixer.VoicesSpan)
                if (voice.Src is WsolaAudioSource stretched) stretched.BeginBlock(1.0, Math.Max(blockStart, voice.StartFrame));
            _mixer.Render(_block, n, new BlockCtx(blockStart, SampleRate, Channels, _plane));
            _block.AsSpan(0, n * Channels).CopyTo(dst.Slice(done * Channels));
            done += n;
        }
    }

    /// <summary>Render the next <paramref name="frames"/> frames into a new interleaved buffer.</summary>
    public float[] Render(int frames)
    {
        var all = new float[Math.Max(0, frames) * Channels];
        Render(all, Math.Max(0, frames));
        return all;
    }
}
