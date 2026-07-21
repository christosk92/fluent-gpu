using System;

namespace FluentGpu.Media;

/// <summary>
/// ReplayGain normalization (spec §7.7) — metadata + ONE reversible scalar (never live AGC). The active
/// <see cref="NormMode"/> selects the per-track or per-album dB (Album is the default: preserves inter-track dynamics
/// for gapless; Track equalizes for shuffle); the result is applied as the per-source gain at the voice, BEFORE the mix.
/// A permanent terminal limiter (<see cref="LimiterStage"/>) catches any clip a positive scalar or EQ boost could cause.
/// </summary>
public static class ReplayGain
{
    /// <summary>The ReplayGain 2.0 reference loudness the tags are measured against (-18 LUFS).</summary>
    public const float TagReferenceLufs = -18f;

    /// <summary>The linear scalar to apply at the voice for <paramref name="info"/> under <paramref name="mode"/>, retargeted
    /// from the tag reference to <paramref name="referenceLufs"/> (e.g. -14 adds +4 dB). <see cref="NormMode.Off"/> → 1.
    /// A pure reversible gain — NOT peak-limited here (the terminal limiter is the clip safety, spec §7.7).</summary>
    public static float ScalarLinear(in ReplayGainInfo info, NormMode mode, float referenceLufs = TagReferenceLufs)
    {
        if (mode == NormMode.Off) return 1f;
        float gainDb = mode == NormMode.Album ? info.AlbumGainDb : info.TrackGainDb;
        float adjustedDb = gainDb + (referenceLufs - TagReferenceLufs);
        return MathF.Pow(10f, adjustedDb / 20f);
    }
}

/// <summary>
/// A leaf source that plays interleaved <c>f32</c> PCM from an in-memory buffer (spec §7.2 <see cref="IAudioSource"/>).
/// The decode edge (<see cref="WavAudioDecoder"/>) fills one, and golden tests drive deterministic vectors through it.
/// Carries its own <see cref="GaplessInfo"/> + <see cref="ReplayGainInfo"/>. Alloc-free reads.
/// </summary>
public sealed class MemoryAudioSource : IAudioSource
{
    private readonly float[] _data;   // interleaved
    private readonly int _channels;
    private readonly long _totalFrames;
    private long _cursor;             // frames consumed

    /// <summary>Create a source over <paramref name="interleaved"/> PCM (<paramref name="channels"/> channels).</summary>
    public MemoryAudioSource(float[] interleaved, int channels, GaplessInfo gapless = default, ReplayGainInfo loudness = default)
    {
        _data = interleaved;
        _channels = Math.Max(1, channels);
        _totalFrames = _data.Length / _channels;
        Gapless = gapless;
        Loudness = loudness;
    }

    /// <summary>The total frame count in the buffer.</summary>
    public long TotalFrames => _totalFrames;
    /// <inheritdoc/>
    public long PositionFrames => _cursor;
    /// <inheritdoc/>
    public bool Exhausted => _cursor >= _totalFrames;
    /// <inheritdoc/>
    public GaplessInfo Gapless { get; }
    /// <inheritdoc/>
    public ReplayGainInfo Loudness { get; }

    /// <summary>Seek to a frame index.</summary>
    public void SeekFrame(long frame) => _cursor = Math.Clamp(frame, 0, _totalFrames);

    /// <inheritdoc/>
    public int Read(Span<float> dst, int channels)
    {
        if (channels != _channels) channels = _channels;
        int wantFrames = dst.Length / channels;
        long remaining = _totalFrames - _cursor;
        int frames = (int)Math.Min(wantFrames, remaining);
        if (frames <= 0) return 0;
        int n = frames * channels;
        _data.AsSpan((int)(_cursor * channels), n).CopyTo(dst);
        _cursor += frames;
        return frames;
    }
}

/// <summary>
/// A deterministic signal-generator source (spec §7.8 visualizer / golden vectors) — a sine (or silence) at a fixed
/// frequency/amplitude for a bounded number of frames, phase-continuous across reads. Real and useful (not a stub): it
/// drives EQ/limiter golden tests and can back a test tone. Alloc-free.
/// </summary>
public sealed class SignalGeneratorSource : IAudioSource
{
    private readonly int _channels;
    private readonly double _freq;
    private readonly float _amp;
    private readonly int _mixRate;
    private readonly long _totalFrames;
    private long _cursor;

    /// <summary>Create a sine generator at <paramref name="freqHz"/>/<paramref name="amplitude"/> for
    /// <paramref name="totalFrames"/> frames (−1 = endless).</summary>
    public SignalGeneratorSource(int channels, int mixRate, double freqHz, float amplitude, long totalFrames)
    {
        _channels = Math.Max(1, channels);
        _mixRate = Math.Max(1, mixRate);
        _freq = freqHz;
        _amp = amplitude;
        _totalFrames = totalFrames;
        Loudness = default;
        Gapless = GaplessInfo.None;
    }

    /// <inheritdoc/>
    public long PositionFrames => _cursor;
    /// <inheritdoc/>
    public bool Exhausted => _totalFrames >= 0 && _cursor >= _totalFrames;
    /// <inheritdoc/>
    public GaplessInfo Gapless { get; }
    /// <inheritdoc/>
    public ReplayGainInfo Loudness { get; }

    /// <inheritdoc/>
    public int Read(Span<float> dst, int channels)
    {
        if (channels != _channels) channels = _channels;
        int wantFrames = dst.Length / channels;
        if (_totalFrames >= 0) wantFrames = (int)Math.Min(wantFrames, _totalFrames - _cursor);
        if (wantFrames <= 0) return 0;

        double w = 2.0 * Math.PI * _freq / _mixRate;
        for (int f = 0; f < wantFrames; f++)
        {
            float s = _amp * (float)Math.Sin(w * (_cursor + f));
            int b = f * channels;
            for (int c = 0; c < channels; c++) dst[b + c] = s;
        }
        _cursor += wantFrames;
        return wantFrames;
    }
}

/// <summary>
/// The gapless trim decorator (spec §8.3): applies encoder-delay/pad trim to an inner source — skips
/// <see cref="GaplessInfo.LeadInFrames"/> at the start and stops <see cref="GaplessInfo.TrailPadFrames"/> early so a
/// butt-join is SAMPLE-ACCURATE across every codec. The emitted length is <see cref="GaplessInfo.ExactFrames"/> when
/// known, else <c>innerTotal − LeadIn − TrailPad</c>. <see cref="PositionFrames"/> is the post-trim (mixer-domain) cursor.
/// Alloc-free.
/// </summary>
public sealed class TrimmingSource : IAudioSource
{
    private readonly IAudioSource _inner;
    private readonly int _channels;
    private readonly int _leadIn;
    private readonly long _emitLimit;   // frames to emit after trim (long.MaxValue when unknown/streaming)
    private long _emitted;
    private bool _leadSkipped;

    /// <summary>Wrap <paramref name="inner"/> with <paramref name="gapless"/> trim. <paramref name="innerTotalFrames"/> is
    /// the untrimmed length (−1 = unknown/streaming: TrailPad is applied only via a known <see cref="GaplessInfo.ExactFrames"/>).</summary>
    public TrimmingSource(IAudioSource inner, GaplessInfo gapless, int channels, long innerTotalFrames = -1)
    {
        _inner = inner;
        _channels = Math.Max(1, channels);
        _leadIn = Math.Max(0, gapless.LeadInFrames);
        Gapless = gapless;
        Loudness = inner.Loudness;

        if (gapless.ExactFrames >= 0) _emitLimit = gapless.ExactFrames;
        else if (innerTotalFrames >= 0) _emitLimit = Math.Max(0, innerTotalFrames - _leadIn - Math.Max(0, gapless.TrailPadFrames));
        else _emitLimit = long.MaxValue;
    }

    /// <inheritdoc/>
    public long PositionFrames => _emitted;
    /// <inheritdoc/>
    public bool Exhausted => _emitted >= _emitLimit || (_leadSkipped && _inner.Exhausted);
    /// <inheritdoc/>
    public GaplessInfo Gapless { get; }
    /// <inheritdoc/>
    public ReplayGainInfo Loudness { get; }

    /// <inheritdoc/>
    public int Read(Span<float> dst, int channels)
    {
        if (channels != _channels) channels = _channels;
        SkipLeadIn(channels);

        long remaining = _emitLimit - _emitted;
        if (remaining <= 0) return 0;

        int wantFrames = (int)Math.Min(dst.Length / channels, remaining);
        if (wantFrames <= 0) return 0;

        int got = _inner.Read(dst[..(wantFrames * channels)], channels);
        if (got <= 0) return 0;
        _emitted += got;
        return got;
    }

    private void SkipLeadIn(int channels)
    {
        if (_leadSkipped) return;
        _leadSkipped = true;
        if (_leadIn <= 0) return;
        // Discard the lead-in frames from the inner source (encoder delay). One-shot, fixed 512-float scratch.
        Span<float> sink = stackalloc float[512];
        long toSkip = (long)_leadIn;
        int perCall = Math.Max(1, sink.Length / channels);
        while (toSkip > 0)
        {
            int chunk = (int)Math.Min(perCall, toSkip);
            int got = _inner.Read(sink[..(chunk * channels)], channels);
            if (got <= 0) break;
            toSkip -= got;
        }
    }
}
