using System;
using System.Threading;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>Crossfade curve family (spec §7.10). Equal-power is the default for uncorrelated material.</summary>
public enum CrossCurve : byte { EqualPower, Linear, Auto }

/// <summary>Loudness normalization mode (spec §7.7). Album is the default (preserves inter-track dynamics for gapless).</summary>
public enum NormMode : byte { Off, Track, Album }

/// <summary>RBJ biquad band type (spec §7.8).</summary>
public enum BiquadType : byte { Peaking, LowShelf, HighShelf, LowPass, HighPass, Notch }

/// <summary>A published visualizer frame (spec §7.3/§7.8) — a copied FFT/level snapshot bound like any other signal.</summary>
public readonly record struct VisualizerFrame(ReadOnlyMemory<float> Magnitudes, float Rms, float Peak)
{
    /// <summary>The empty (silence) frame.</summary>
    public static VisualizerFrame Silence { get; } = new(ReadOnlyMemory<float>.Empty, 0f, 0f);
}

/// <summary>What accompanied the last published spectrum: a monotonic <paramref name="Sequence"/> (0 = nothing yet),
/// the band count, whether the session was muted/silenced (the UI shows its idle breath), the PRE-gain RMS of the
/// analysed window (what Tape's meter and Field's breath use — the level tap's RMS is post-volume), how far behind the
/// newest rendered sample the window ended (frames — a diagnostic of the alignment actually applied), the analysis
/// cost, and whether a spectrum lease is live at all. <paramref name="Flux"/> is the spectral flux of this publish (mean
/// band rise in dB, <see cref="OnsetDetector"/>); <paramref name="OnsetSequence"/> increments on every detected onset, so a
/// reader compares it with the last one it saw; <paramref name="OnsetStrength"/> is that onset's 0..1 strength.</summary>
public readonly record struct SpectrumInfo(long Sequence, int BandCount, bool Muted, float WindowRms, long AlignFrames, float FftMs, bool Live,
    float Flux = 0f, long OnsetSequence = 0, float OnsetStrength = 0f);

/// <summary>What accompanied the last published waveform: the same <paramref name="Sequence"/> as the band publish it
/// came with, the sample count, the session's sample rate, and whether a spectrum lease is live.</summary>
public readonly record struct WaveformInfo(long Sequence, int SampleCount, int SampleRate, bool Live);

/// <summary>One EQ band (spec §7.10). Each parameter is a signal — a slider write ramps smoothly (set-vs-ramp is a
/// value, not a topology edit — no zipper noise).</summary>
public sealed class EqBand
{
    /// <summary>The band's gain in dB (a hot signal; the RT plane smooths it).</summary>
    public FloatSignal GainDb { get; } = new(0f);
    /// <summary>The band's center/corner frequency in Hz.</summary>
    public FloatSignal FreqHz { get; }
    /// <summary>The band's Q.</summary>
    public FloatSignal Q { get; } = new(1f);
    /// <summary>The biquad type.</summary>
    public BiquadType Type { get; init; } = BiquadType.Peaking;

    /// <summary>Create a band centered at <paramref name="freqHz"/>.</summary>
    public EqBand(float freqHz) => FreqHz = new FloatSignal(freqHz);
}

/// <summary>An EQ preset (spec §7.10) — an ordered set of bands with default gains/frequencies.</summary>
public sealed record EqPreset(float[] FrequenciesHz, float[] GainsDb)
{
    /// <summary>The canonical 5-band preset (31/125/500/2k/8k Hz), flat by default.</summary>
    public static EqPreset FiveBand(bool defaults = true)
        => new(new[] { 31f, 125f, 500f, 2000f, 8000f }, defaults ? new float[5] : new float[5]);
}

/// <summary>The graphic equalizer surface (spec §7.10). Each band's Gain/Freq/Q is a signal.</summary>
public sealed class Equalizer
{
    /// <summary>Whether the EQ is enabled.</summary>
    public Signal<bool> Enabled { get; } = new(false);
    /// <summary>The bands (created by <see cref="Apply"/> or a preset).</summary>
    public EqBand[] Bands { get; private set; } = Array.Empty<EqBand>();

    /// <summary>Apply a preset — (re)creates the band set and seeds gains/frequencies.</summary>
    public void Apply(EqPreset preset)
    {
        var bands = new EqBand[preset.FrequenciesHz.Length];
        for (int i = 0; i < bands.Length; i++)
        {
            bands[i] = new EqBand(preset.FrequenciesHz[i]);
            if (i < preset.GainsDb.Length) bands[i].GainDb.Value = preset.GainsDb[i];
        }
        Bands = bands;
        Enabled.Value = true;
    }
}

/// <summary>
/// The player effects surface (spec §7.10). The PCM audio backend exposes a live graph behind these signals; the MF
/// video backend returns an inert null-object (<see cref="NullAudioEffects"/>) — the two engines never co-mix.
/// </summary>
public interface IAudioEffects
{
    /// <summary>The graphic equalizer.</summary>
    Equalizer Equalizer { get; }
    /// <summary>The crossfade overlap in ms (0 == gapless).</summary>
    FloatSignal CrossfadeMs { get; }
    /// <summary>The crossfade curve.</summary>
    Signal<CrossCurve> CrossfadeCurve { get; }
    /// <summary>The loudness normalization mode.</summary>
    Signal<NormMode> Normalization { get; }
    /// <summary>The reference LUFS target (-11 | -14 | -17 | -19).</summary>
    Signal<float> ReferenceLufs { get; }
    /// <summary>The L/R balance (-1..+1).</summary>
    FloatSignal Balance { get; }
    /// <summary>Whether rate changes preserve pitch.</summary>
    Signal<bool> PreservePitchOnRate { get; }
    /// <summary>The published LEVEL frame (RMS/peak every control tick). <see cref="VisualizerFrame.Magnitudes"/> stays
    /// EMPTY (reserved): the spectrum is read ONLY through <see cref="CopySpectrum"/>, so a bound frame never tears.</summary>
    IReadSignal<VisualizerFrame> Visualizer { get; }
    /// <summary>Keep LEVEL analysis (RMS/peak) active while a visible consumer needs it. Dispose when hidden or inactive.</summary>
    IDisposable AcquireVisualizer();
    /// <summary>Keep SPECTRUM analysis (the FFT tier) active while a visible consumer needs it; implies the level tap.
    /// Dispose when hidden, paused-for-good, occluded or under reduced motion.</summary>
    IDisposable AcquireSpectrum();
    /// <summary>Copy the latest band magnitudes (dB, <see cref="SpectrumAnalyzer.FloorDb"/>..<see cref="SpectrumAnalyzer.CeilingDb"/>)
    /// into <paramref name="destination"/> without tearing (one lock, one memcpy of the FRONT buffer). Returns the count
    /// copied — 0 before the first publish or without a lease. THE ONLY spectrum read path; the UI PULLS it on its own
    /// cadence, nothing pushes.</summary>
    int CopySpectrum(Span<float> destination, out SpectrumInfo info);
    /// <summary>Copy the latest time-domain samples (mono, −1..1, the centre <see cref="AudioEffects.WaveformSamples"/> of the
    /// same latency-aligned window the bands came from) into <paramref name="destination"/>. Same lease, same lock, same
    /// sequence as <see cref="CopySpectrum"/>; 0 before the first publish or without a lease.</summary>
    int CopyWaveform(Span<float> destination, out WaveformInfo info);
    /// <summary>The user's playback-sync offset in milliseconds (positive reads the window EARLIER, for a device that
    /// adds latency the clock cannot see, e.g. Bluetooth). Clamped to ±500. Cross-thread safe.</summary>
    float SpectrumOffsetMs { get; set; }
}

/// <summary>The live effects surface backing the PCM audio player (spec §7.10). In M0 these are the signal-plane values
/// (topology vs param split); the DSP graph that consumes them lands in M2/M3.</summary>
public sealed class AudioEffects : IAudioEffects
{
    private readonly object _visualizerGate = new(); // never acquired on the audio render thread
    private int _visualizerConsumers;
    private long _nextVisualizerEpoch, _visualizerEpoch, _visualizerSource;

    // ── the SPECTRUM tier: a second demand count over the SAME source token; magnitudes are double-buffered ───────────
    private int _spectrumConsumers;
    private long _spectrumEpoch;                        // 0 = no spectrum demand; rotates with the source like _visualizerEpoch
    private readonly float[][] _magnitudes = [new float[SpectrumAnalyzer.DefaultBandCount], new float[SpectrumAnalyzer.DefaultBandCount]];
    private int _magnitudeFront;                        // the readable buffer (under _visualizerGate)
    private int _magnitudeCount;                        // 0 = nothing published since the lease began
    private long _spectrumSequence;
    private bool _spectrumMuted;
    private float _spectrumWindowRms, _spectrumFftMs;
    private long _spectrumAlignFrames;
    private float _spectrumFlux, _onsetStrength;
    private long _onsetSequence;

    /// <summary>How many time-domain samples a publish carries (the centre of the 2048-sample analysis window).</summary>
    public const int WaveformSamples = 1024;
    private readonly float[][] _waveform = [new float[WaveformSamples], new float[WaveformSamples]];
    private int _waveformFront, _waveformCount, _waveformRate;
    private int _spectrumOffsetMsBits;                  // float bits, Volatile: written by the UI, read on the clock thread

    private readonly IReadSignal<VisualizerFrame> _visualizerView;
    /// <summary>Create an effects surface with no level-analysis demand.</summary>
    public AudioEffects() => _visualizerView = new VisualizerView(this);

    /// <inheritdoc/>
    public IDisposable AcquireVisualizer()
    {
        lock (_visualizerGate)
        {
            if (_visualizerConsumers++ == 0)
            {
                _visualizer.Value = VisualizerFrame.Silence;
                Volatile.Write(ref _visualizerEpoch, ++_nextVisualizerEpoch);
            }
        }
        return new VisualizerLease(this);
    }

    private void ReleaseVisualizer()
    {
        lock (_visualizerGate)
        {
            if (--_visualizerConsumers != 0) return;
            Volatile.Write(ref _visualizerEpoch, 0);
            _visualizer.Value = VisualizerFrame.Silence;
        }
    }

    internal long BindVisualizerSource()
    {
        lock (_visualizerGate)
        {
            long source = ++_visualizerSource;
            _magnitudeCount = 0;
            _visualizer.Value = VisualizerFrame.Silence;
            Volatile.Write(ref _visualizerEpoch, _visualizerConsumers == 0 ? 0 : ++_nextVisualizerEpoch);
            Volatile.Write(ref _spectrumEpoch, _spectrumConsumers == 0 ? 0 : ++_nextVisualizerEpoch);
            return source;
        }
    }

    internal long VisualizerDemand(long source)
        => source == Volatile.Read(ref _visualizerSource) ? Volatile.Read(ref _visualizerEpoch) : 0;

    internal bool PublishVisualizerFrame(long source, long epoch, float rms, float peak)
    {
        lock (_visualizerGate)
        {
            if (epoch == 0 || epoch != _visualizerEpoch || source != _visualizerSource) return false;
            _visualizer.Value = new VisualizerFrame(ReadOnlyMemory<float>.Empty, rms, peak);
            return true;
        }
    }

    /// <inheritdoc/>
    public float SpectrumOffsetMs
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _spectrumOffsetMsBits));
        set => Volatile.Write(ref _spectrumOffsetMsBits, BitConverter.SingleToInt32Bits(Math.Clamp(value, -500f, 500f)));
    }

    /// <inheritdoc/>
    public IDisposable AcquireSpectrum()
    {
        IDisposable level = AcquireVisualizer();          // the FFT tier implies the level tap (Tape's meter, Field's breath)
        lock (_visualizerGate)
        {
            if (_spectrumConsumers++ == 0)
            {
                _magnitudeCount = 0;
                _waveformCount = 0;
                Volatile.Write(ref _spectrumEpoch, ++_nextVisualizerEpoch);
            }
        }
        return new SpectrumLease(this, level);
    }

    private void ReleaseSpectrum()
    {
        lock (_visualizerGate)
        {
            if (--_spectrumConsumers != 0) return;
            Volatile.Write(ref _spectrumEpoch, 0);
            _magnitudeCount = 0;                            // CopySpectrum returns 0 from here on; the level frame is untouched
            _waveformCount = 0;
        }
    }

    /// <summary>The RT thread's gate for the spectrum ring: the current spectrum epoch iff <paramref name="source"/> is the
    /// bound session, else 0. Two volatile reads, no lock.</summary>
    internal long SpectrumDemand(long source)
        => source == Volatile.Read(ref _visualizerSource) ? Volatile.Read(ref _spectrumEpoch) : 0;

    /// <summary>Control-thread publish of one analysed window (never RT). Writes the BACK buffer, swaps, bumps the
    /// sequence; false when the lease or the source has moved on. Never touches <see cref="_visualizer"/> (O2).</summary>
    internal bool PublishSpectrum(long source, long epoch, ReadOnlySpan<float> bandsDb, bool muted, float windowRms, long alignFrames, float fftMs,
        ReadOnlySpan<float> waveform = default, int sampleRate = 0, float flux = 0f, bool onset = false, float onsetStrength = 0f)
    {
        lock (_visualizerGate)
        {
            if (epoch == 0 || epoch != _spectrumEpoch || source != _visualizerSource) return false;
            int back = _magnitudeFront ^ 1;
            int n = Math.Min(bandsDb.Length, _magnitudes[back].Length);
            bandsDb[..n].CopyTo(_magnitudes[back]);
            _magnitudeFront = back;
            _magnitudeCount = n;
            if (!waveform.IsEmpty)
            {
                int wback = _waveformFront ^ 1;
                int w = Math.Min(waveform.Length, WaveformSamples);
                waveform[..w].CopyTo(_waveform[wback]);
                _waveformFront = wback;
                _waveformCount = w;
                _waveformRate = sampleRate;
            }
            _spectrumSequence++;
            _spectrumMuted = muted;
            _spectrumWindowRms = windowRms;
            _spectrumAlignFrames = alignFrames;
            _spectrumFftMs = fftMs;
            _spectrumFlux = flux;
            if (onset) { _onsetSequence++; _onsetStrength = onsetStrength; }
            return true;
        }
    }

    /// <inheritdoc/>
    public int CopySpectrum(Span<float> destination, out SpectrumInfo info)
    {
        lock (_visualizerGate)
        {
            int n = Math.Min(_magnitudeCount, destination.Length);
            if (n > 0) _magnitudes[_magnitudeFront].AsSpan(0, n).CopyTo(destination);
            info = new SpectrumInfo(_spectrumSequence, n, _spectrumMuted, _spectrumWindowRms, _spectrumAlignFrames, _spectrumFftMs, _spectrumEpoch != 0,
                _spectrumFlux, _onsetSequence, _onsetStrength);
            return n;
        }
    }

    /// <inheritdoc/>
    public int CopyWaveform(Span<float> destination, out WaveformInfo info)
    {
        lock (_visualizerGate)
        {
            int n = Math.Min(_waveformCount, destination.Length);
            if (n > 0) _waveform[_waveformFront].AsSpan(0, n).CopyTo(destination);
            info = new WaveformInfo(_spectrumSequence, n, _waveformRate, _spectrumEpoch != 0);
            return n;
        }
    }

    private sealed class SpectrumLease(AudioEffects owner, IDisposable level) : IDisposable
    {
        private AudioEffects? _owner = owner;
        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseSpectrum();
            level.Dispose();   // idempotent (VisualizerLease)
        }
    }

    private sealed class VisualizerLease(AudioEffects owner) : IDisposable
    {
        private AudioEffects? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseVisualizer();
    }

    // Signal<T> itself is UI-owned. Serialize this explicitly cross-thread tap's reads and non-RT writes,
    // so the two floats (and reserved spectrum field) cannot tear when the UI pulls a control publication.
    private sealed class VisualizerView(AudioEffects owner) : IReadSignal<VisualizerFrame>
    {
        public VisualizerFrame Value { get { lock (owner._visualizerGate) return owner._visualizer.Value; } }
        public VisualizerFrame Peek() { lock (owner._visualizerGate) return owner._visualizer.Peek(); }
    }
    /// <inheritdoc/>
    public Equalizer Equalizer { get; } = new();
    /// <inheritdoc/>
    public FloatSignal CrossfadeMs { get; } = new(0f);
    /// <inheritdoc/>
    public Signal<CrossCurve> CrossfadeCurve { get; } = new(CrossCurve.EqualPower);
    /// <inheritdoc/>
    public Signal<NormMode> Normalization { get; } = new(NormMode.Album);
    /// <inheritdoc/>
    public Signal<float> ReferenceLufs { get; } = new(-14f);
    /// <inheritdoc/>
    public FloatSignal Balance { get; } = new(0f);
    /// <inheritdoc/>
    public Signal<bool> PreservePitchOnRate { get; } = new(true);
    private readonly Signal<VisualizerFrame> _visualizer = new(VisualizerFrame.Silence);
    /// <inheritdoc/>
    public IReadSignal<VisualizerFrame> Visualizer => _visualizerView;
}

/// <summary>The inert effects null-object returned by the MF video backend (spec §7.10) — every knob exists but does
/// nothing, so a control kit binds it uniformly and never null-checks.</summary>
public sealed class NullAudioEffects : IAudioEffects
{
    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    private static readonly IDisposable NoVisualizer = new EmptyLease();
    /// <inheritdoc/>
    public IDisposable AcquireVisualizer() => NoVisualizer;
    /// <inheritdoc/>
    public IDisposable AcquireSpectrum() => NoVisualizer;
    /// <inheritdoc/>
    public int CopySpectrum(Span<float> destination, out SpectrumInfo info) { info = default; return 0; }
    /// <inheritdoc/>
    public int CopyWaveform(Span<float> destination, out WaveformInfo info) { info = default; return 0; }
    /// <inheritdoc/>
    public float SpectrumOffsetMs { get; set; }
    /// <summary>The shared inert instance.</summary>
    public static NullAudioEffects Instance { get; } = new();

    /// <inheritdoc/>
    public Equalizer Equalizer { get; } = new();
    /// <inheritdoc/>
    public FloatSignal CrossfadeMs { get; } = new(0f);
    /// <inheritdoc/>
    public Signal<CrossCurve> CrossfadeCurve { get; } = new(CrossCurve.EqualPower);
    /// <inheritdoc/>
    public Signal<NormMode> Normalization { get; } = new(NormMode.Off);
    /// <inheritdoc/>
    public Signal<float> ReferenceLufs { get; } = new(-14f);
    /// <inheritdoc/>
    public FloatSignal Balance { get; } = new(0f);
    /// <inheritdoc/>
    public Signal<bool> PreservePitchOnRate { get; } = new(true);
    private readonly Signal<VisualizerFrame> _visualizer = new(VisualizerFrame.Silence);
    /// <inheritdoc/>
    public IReadSignal<VisualizerFrame> Visualizer => _visualizer;
}
