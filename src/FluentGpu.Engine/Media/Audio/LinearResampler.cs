using System;

namespace FluentGpu.Media;

/// <summary>
/// A stateful linear-interpolation resampler (spec §7.1: "every source resamples INTO the fixed mix format at the decode
/// edge"). Block-continuous: it carries a 1-frame history + fractional phase across <see cref="Process"/> calls so a
/// stream resamples seamlessly in arbitrary block sizes. When the rates match it is a pass-through (<see cref="IsActive"/>
/// == false). Interleaved <c>f32</c>, alloc-free per block. Linear is the M2 choice; a windowed-sinc SRC is a later
/// quality refinement (see report).
/// </summary>
public sealed class LinearResampler
{
    private readonly int _fromRate;
    private readonly int _toRate;
    private readonly int _channels;
    private readonly double _step;        // input frames advanced per output frame
    private readonly float[] _prev;       // input frame at index -1 (the last frame of the previous block)
    private double _pos;                  // fractional input index within the current block
    private bool _primed;

    /// <summary>Create a resampler from <paramref name="fromRate"/> Hz to <paramref name="toRate"/> Hz for
    /// <paramref name="channels"/> channels.</summary>
    public LinearResampler(int fromRate, int toRate, int channels)
    {
        _fromRate = fromRate;
        _toRate = toRate;
        _channels = Math.Max(1, channels);
        _step = toRate > 0 ? (double)fromRate / toRate : 1.0;
        _prev = new float[_channels];
    }

    /// <summary>True when the rates differ (the resampler does real work; otherwise <see cref="Process"/> is a copy).</summary>
    public bool IsActive => _fromRate != _toRate && _fromRate > 0 && _toRate > 0;

    /// <summary>The added latency in OUTPUT frames (the 1-frame interpolation history).</summary>
    public int LatencySamples => IsActive ? 1 : 0;

    /// <summary>The worst-case output-frame capacity needed for <paramref name="inFrames"/> input frames (for sizing dst).</summary>
    public int MaxOutFrames(int inFrames) => IsActive ? (int)Math.Ceiling(inFrames / _step) + 1 : inFrames;

    /// <summary>Reset all continuity state (a seek/discontinuity — the caller declicks).</summary>
    public void Reset()
    {
        Array.Clear(_prev);
        _pos = 0;
        _primed = false;
    }

    /// <summary>Resample <paramref name="inFrames"/> interleaved input frames from <paramref name="src"/> into
    /// <paramref name="dst"/>; returns the number of output frames produced (short — the tail carries to the next call).</summary>
    public int Process(ReadOnlySpan<float> src, int inFrames, Span<float> dst)
    {
        int ch = _channels;
        if (!IsActive)
        {
            int n = inFrames * ch;
            src[..n].CopyTo(dst);
            return inFrames;
        }
        if (inFrames <= 0) return 0;

        double p = _pos;
        int outFrames = 0;
        int maxOut = dst.Length / ch;

        while (outFrames < maxOut)
        {
            int i0 = (int)Math.Floor(p);
            int i1 = i0 + 1;
            if (i1 > inFrames - 1) break;   // need src[i1] — not available until the next block

            float frac = (float)(p - i0);
            int ob = outFrames * ch;
            for (int c = 0; c < ch; c++)
            {
                float s0 = i0 < 0 ? _prev[c] : src[i0 * ch + c];
                float s1 = src[i1 * ch + c];
                dst[ob + c] = s0 + (s1 - s0) * frac;
            }
            outFrames++;
            p += _step;
        }

        // Carry: remember the last input frame and rebase the phase to the next block's index domain.
        int last = (inFrames - 1) * ch;
        for (int c = 0; c < ch; c++) _prev[c] = src[last + c];
        _pos = p - inFrames;
        _primed = true;
        return outFrames;
    }
}
