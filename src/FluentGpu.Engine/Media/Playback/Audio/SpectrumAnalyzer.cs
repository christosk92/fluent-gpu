using System;
using System.Numerics;

namespace FluentGpu.Media;

/// <summary>
/// The visualizer's spectrum analyzer (spec §7.8: "the Tap node's lock-free ring; a non-RT tick runs the FFT"). A
/// radix-2 iterative FFT over a Hann-windowed 2048-sample mono window, reduced to 48 log-spaced bands between 40 Hz
/// and min(16 kHz, 0.45·rate), reported in dB with a +3 dB/octave pink tilt and clamped to [−80, +6]. Every table
/// (window, twiddles, bit-reversal, band edges, tilt) is built ONCE in the constructor; <see cref="Analyze"/>
/// allocates nothing. Pure math, AOT-safe. CONTROL-THREAD ONLY: the RT path only fills the <see cref="SpectrumRing"/>.
/// Normalisation (Parseval): band power is Σ|X_k|² over the band's bins × 4/(N·Σw²), so a full-scale sine whose Hann
/// main lobe falls inside ONE band reads 0 dB in that band (before the tilt) — the one-sided windowed-signal energy
/// N·Σw²·A²/4 maps to A². The clamp to [FloorDb, CeilingDb] is applied AFTER the tilt.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int DefaultFftSize = 2048;
    public const int DefaultBandCount = 48;
    public const float MinHz = 40f;
    public const float MaxHzCap = 16_000f;
    public const float FloorDb = -80f;
    public const float CeilingDb = 6f;
    public const float TiltDbPerOctave = 3f;

    private readonly int _n, _bands, _rate;
    private readonly float[] _window, _re, _im, _cos, _sin, _tilt;
    private readonly int[] _bitRev, _bandLo, _bandHi;   // bin range [lo, hi) per band
    private readonly float _norm;

    /// <summary>Window length in samples (a power of two).</summary>
    public int FftSize => _n;
    /// <summary>Bands per analysis.</summary>
    public int BandCount => _bands;
    /// <summary>The rate the band edges were laid out for; a session re-creates the analyzer when its rate changes.</summary>
    public int SampleRate => _rate;
    /// <summary>The top band's upper edge in Hz.</summary>
    public float MaxHz { get; }

    public SpectrumAnalyzer(int sampleRate, int fftSize = DefaultFftSize, int bandCount = DefaultBandCount)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (fftSize < 16 || !BitOperations.IsPow2(fftSize)) throw new ArgumentOutOfRangeException(nameof(fftSize), "a power of two ≥ 16");
        if (bandCount < 1 || bandCount > fftSize / 4) throw new ArgumentOutOfRangeException(nameof(bandCount));
        _n = fftSize;
        _bands = bandCount;
        _rate = sampleRate;
        _window = new float[fftSize];
        _re = new float[fftSize];
        _im = new float[fftSize];
        _cos = new float[fftSize / 2];
        _sin = new float[fftSize / 2];
        _bitRev = new int[fftSize];
        _bandLo = new int[bandCount];
        _bandHi = new int[bandCount];
        _tilt = new float[bandCount];

        double sumSq = 0;
        for (int i = 0; i < fftSize; i++)
        {
            double w = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / fftSize));   // periodic Hann
            _window[i] = (float)w;
            sumSq += w * w;
        }
        _norm = (float)(4.0 / (fftSize * sumSq));   // Parseval: Σ_band |X_k|² of a unit sine → 1.0 → 0 dB (the lobe's energy, not the peak bin)

        for (int k = 0; k < fftSize / 2; k++)
        {
            double a = -2.0 * Math.PI * k / fftSize;
            _cos[k] = (float)Math.Cos(a);
            _sin[k] = (float)Math.Sin(a);
        }
        int log2n = BitOperations.Log2((uint)fftSize);
        for (int i = 0; i < fftSize; i++) _bitRev[i] = (int)(ReverseBits((uint)i) >> (32 - log2n));

        MaxHz = MathF.Min(MaxHzCap, 0.45f * sampleRate);
        double binHz = (double)sampleRate / fftSize;
        double ratio = MaxHz / MinHz;
        int prevHi = Math.Max(1, (int)Math.Round(MinHz / binHz));
        for (int b = 0; b < bandCount; b++)
        {
            double fLo = MinHz * Math.Pow(ratio, (double)b / bandCount);
            double fHi = MinHz * Math.Pow(ratio, (double)(b + 1) / bandCount);
            int lo = Math.Max(prevHi, (int)Math.Round(fLo / binHz));
            int hi = Math.Max(lo + 1, (int)Math.Round(fHi / binHz));
            hi = Math.Min(hi, fftSize / 2);
            lo = Math.Min(lo, hi - 1);
            _bandLo[b] = lo;
            _bandHi[b] = hi;
            prevHi = hi;
            double fc = Math.Sqrt(lo * binHz * (hi * binHz));   // the band's actual bin range, the same centre BandCenterHz reports
            _tilt[b] = (float)(TiltDbPerOctave * Math.Log2(fc / MinHz));
        }
    }

    /// <summary>The band whose [lo, hi) bin range holds <paramref name="hz"/>, or −1 outside every band.</summary>
    public int BandOf(float hz)
    {
        int bin = (int)Math.Round(hz * _n / (double)_rate);
        for (int b = 0; b < _bands; b++) if (bin >= _bandLo[b] && bin < _bandHi[b]) return b;
        return -1;
    }

    /// <summary>The geometric centre of band <paramref name="b"/> in Hz.</summary>
    public float BandCenterHz(int b)
    {
        double binHz = (double)_rate / _n;
        return (float)Math.Sqrt(_bandLo[b] * binHz * (_bandHi[b] * binHz));
    }

    /// <summary>Analyse one window. <paramref name="mono"/> must be exactly <see cref="FftSize"/> samples;
    /// <paramref name="bandsDb"/> receives <see cref="BandCount"/> values in [FloorDb, CeilingDb]. Zero allocation.</summary>
    public void Analyze(ReadOnlySpan<float> mono, Span<float> bandsDb)
    {
        if (mono.Length != _n) throw new ArgumentException("the window must be exactly FftSize samples", nameof(mono));
        if (bandsDb.Length < _bands) throw new ArgumentException("needs BandCount slots", nameof(bandsDb));
        float[] re = _re, im = _im, w = _window;
        int[] rev = _bitRev;
        int n = _n;
        for (int i = 0; i < n; i++)
        {
            int j = rev[i];
            re[j] = mono[i] * w[i];
            im[j] = 0f;
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1, step = n / size;
            for (int start = 0; start < n; start += size)
            {
                for (int k = 0, t = 0; k < half; k++, t += step)
                {
                    float c = _cos[t], s = _sin[t];
                    int a = start + k, b = a + half;
                    float tr = re[b] * c - im[b] * s;
                    float ti = re[b] * s + im[b] * c;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
        float norm = _norm;
        for (int b = 0; b < _bands; b++)
        {
            float p = 0f;
            for (int k = _bandLo[b], hi = _bandHi[b]; k < hi; k++) p += re[k] * re[k] + im[k] * im[k];
            float db = 10f * MathF.Log10(p * norm + 1e-12f) + _tilt[b];
            bandsDb[b] = Math.Clamp(db, FloorDb, CeilingDb);
        }
    }

    private static uint ReverseBits(uint v)
    {
        v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
        v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
        v = ((v >> 4) & 0x0F0F0F0Fu) | ((v & 0x0F0F0F0Fu) << 4);
        v = ((v >> 8) & 0x00FF00FFu) | ((v & 0x00FF00FFu) << 8);
        return (v >> 16) | (v << 16);
    }
}
