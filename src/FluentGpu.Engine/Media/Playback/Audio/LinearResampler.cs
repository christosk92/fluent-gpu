using System;

namespace FluentGpu.Media;

/// <summary>
/// Result of one <see cref="LinearResampler.Process"/> call — both sides of the rate conversion so the caller can
/// retain unread input when <c>dst</c> fills before the source block is exhausted.
/// </summary>
public readonly struct ResampleResult
{
    /// <summary>Output frames written into <c>dst</c>.</summary>
    public int Produced { get; init; }
    /// <summary>Input frames fully consumed from <c>src</c> (caller may drop these; retain <c>src[Consumed..]</c>).</summary>
    public int Consumed { get; init; }

    public ResampleResult(int produced, int consumed) { Produced = produced; Consumed = consumed; }
}

/// <summary>
/// A stateful linear-interpolation resampler (spec §7.1: "every source resamples INTO the fixed mix format at the decode
/// edge"). Block-continuous: it carries a 1-frame history + fractional phase across <see cref="Process"/> calls so a
/// stream resamples seamlessly in arbitrary block sizes. When the rates match it is a pass-through (<see cref="IsActive"/>
/// == false). Interleaved <c>f32</c>, alloc-free per block. Linear is the M2 choice; a windowed-sinc SRC is a later
/// quality refinement (see report).
/// <para>
/// <see cref="Process"/> returns <see cref="ResampleResult"/> with both produced-output and consumed-input counts. When
/// <c>dst</c> fills mid-block the caller MUST retain <c>src[Consumed..]</c> and pass it as the prefix of the next call —
/// treating the whole block as consumed discarded ~2 source frames every production 44.1→48 pump (continuous grit).
/// </para>
/// </summary>
public sealed class LinearResampler
{
    private readonly int _fromRate;
    private readonly int _toRate;
    private readonly int _channels;
    private readonly double _step;        // input frames advanced per output frame
    private readonly float[] _prev;       // input frame at index -1 (cross-block history when phase is in [-1, 0))
    private double _pos;                  // fractional input index within the current src block

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

    /// <summary>
    /// Largest source-frame pull whose <see cref="MaxOutFrames"/> fits in <paramref name="wantOutFrames"/> output slots.
    /// Decoders use this so the common path fully consumes each pull (defense in depth); <see cref="ResampleResult.Consumed"/>
    /// still covers short/partial <c>dst</c> pumps.
    /// </summary>
    public int SrcFramesForOutput(int wantOutFrames)
    {
        if (!IsActive) return Math.Max(0, wantOutFrames);
        if (wantOutFrames <= 1) return 2;   // linear interp needs a pair
        // MaxOutFrames(n) = ceil(n/step)+1 <= want  ⇒  ceil(n/step) <= want-1  ⇒  n <= (want-1)*step
        int n = (int)Math.Floor((wantOutFrames - 1) * _step);
        return Math.Max(2, n);
    }

    /// <summary>Reset all continuity state (a seek/discontinuity — the caller declicks and drops any retained input).</summary>
    public void Reset()
    {
        Array.Clear(_prev);
        _pos = 0;
    }

    /// <summary>Resample <paramref name="inFrames"/> interleaved input frames from <paramref name="src"/> into
    /// <paramref name="dst"/>. Returns produced output frames and how many input frames were consumed — the caller
    /// retains <c>src[Consumed..]</c> for the next call when Consumed &lt; inFrames.</summary>
    public ResampleResult Process(ReadOnlySpan<float> src, int inFrames, Span<float> dst)
    {
        int ch = _channels;
        if (!IsActive)
        {
            // Defense in depth (spec §7.1): clamp untrusted counts to the buffers rather than throw — a short copy is
            // always safe; the real fix for a seek-torn state is worker-routed seek, not this branch.
            int n = Math.Min(inFrames * ch, Math.Min(src.Length, dst.Length));
            if (n <= 0) return default;
            src[..n].CopyTo(dst);
            int frames = n / ch;
            return new ResampleResult(frames, frames);
        }
        if (inFrames * ch > src.Length) inFrames = src.Length / ch;
        if (inFrames <= 0) return default;

        double p = _pos;
        if (p < -1) p = -1;   // torn Reset can leave a wild negative phase — pinning p ≥ -1 keeps i1 ≥ 0
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

        bool exhausted = ((int)Math.Floor(p) + 1) > inFrames - 1;
        int consumed;
        if (exhausted)
        {
            // All input used for interpolation history — classic cross-block carry into the next src block.
            int last = (inFrames - 1) * ch;
            for (int c = 0; c < ch; c++) _prev[c] = src[last + c];
            _pos = p - inFrames;
            consumed = inFrames;
        }
        else
        {
            // maxOut: caller retains src[consumed..] as the next prefix. Rebase phase into that retained window.
            int keepFrom = (int)Math.Floor(p);
            if (keepFrom < 0)
            {
                // Still interpolating against _prev; consume nothing from this src.
                consumed = 0;
                _pos = p;
            }
            else
            {
                consumed = keepFrom;
                _pos = p - keepFrom;   // ∈ [0, 1)
            }
        }

        return new ResampleResult(outFrames, consumed);
    }
}
