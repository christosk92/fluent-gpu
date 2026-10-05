using System;

namespace FluentGpu.Media;

/// <summary>
/// Spectral flux and onset detection over the analyser's published bands — the beat signal a visualizer needs when a
/// track has no precomputed beat grid. Flux is the half-wave-rectified mean band rise between two consecutive frames
/// (dB): <c>Σ max(0, band[i] − prev[i]) / n</c>. An onset fires when the flux clears <c>mean + ThresholdSigma·σ</c> of
/// the recent history and the refractory window since the last onset has elapsed.
/// <para><b>Threads.</b> Control thread only (the same thread as <see cref="SpectrumAnalyzer.Analyze"/>). Allocation
/// happens in the constructor only; <see cref="Step"/> allocates nothing.</para>
/// </summary>
public sealed class OnsetDetector
{
    /// <summary>Frames of flux history the threshold adapts over (≈1.5 s at the ~15 ms control hop).</summary>
    public const int HistoryLength = 96;
    /// <summary>How many deviations above the mean a flux value must reach to count as an onset.</summary>
    public const float ThresholdSigma = 1.5f;
    /// <summary>The shortest gap between two onsets.</summary>
    public const double RefractoryMs = 100.0;
    /// <summary>A flux below this (dB) is never an onset: it keeps the noise floor of a quiet passage out.</summary>
    public const float MinFluxDb = 0.5f;
    /// <summary>History frames needed before an onset may fire (the first frames have no baseline).</summary>
    public const int WarmupFrames = 8;

    private readonly float[] _prev;
    private readonly float[] _history = new float[HistoryLength];
    private int _count, _head;
    private bool _primed;
    private double _lastOnsetMs = double.NegativeInfinity;

    public OnsetDetector(int bandCount) => _prev = new float[bandCount];

    /// <summary>Forget the previous frame and the history (a seek, a track change, a re-armed lease).</summary>
    public void Reset()
    {
        _primed = false;
        _count = 0;
        _head = 0;
        _lastOnsetMs = double.NegativeInfinity;
    }

    /// <summary>Fold one band frame. Returns true when this frame is an onset; <paramref name="flux"/> is always set,
    /// <paramref name="strength"/> is 0..1 (how far above the threshold, in deviations, capped at three).</summary>
    public bool Step(ReadOnlySpan<float> bandsDb, double nowMs, out float flux, out float strength)
    {
        int n = Math.Min(bandsDb.Length, _prev.Length);
        strength = 0f;
        if (!_primed)
        {
            bandsDb[..n].CopyTo(_prev);
            _primed = true;
            flux = 0f;
            return false;
        }

        float rise = 0f;
        for (int i = 0; i < n; i++)
        {
            float d = bandsDb[i] - _prev[i];
            if (d > 0f) rise += d;
            _prev[i] = bandsDb[i];
        }
        flux = n > 0 ? rise / n : 0f;

        // The threshold is taken over the history BEFORE this frame joins it.
        float mean = 0f, var = 0f;
        for (int i = 0; i < _count; i++) mean += _history[i];
        if (_count > 0) mean /= _count;
        for (int i = 0; i < _count; i++) { float e = _history[i] - mean; var += e * e; }
        float sigma = _count > 1 ? MathF.Sqrt(var / (_count - 1)) : 0f;

        _history[_head] = flux;
        _head = (_head + 1) % HistoryLength;
        if (_count < HistoryLength) _count++;

        if (_count <= WarmupFrames || flux < MinFluxDb) return false;
        float threshold = mean + ThresholdSigma * sigma;
        if (flux <= threshold || nowMs - _lastOnsetMs < RefractoryMs) return false;
        _lastOnsetMs = nowMs;
        strength = sigma > 1e-4f ? MathF.Min(1f, (flux - mean) / (3f * sigma)) : 1f;
        return true;
    }
}
