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
    /// <summary>The published visualizer frame.</summary>
    IReadSignal<VisualizerFrame> Visualizer { get; }
    /// <summary>Keep level analysis active while a visible consumer needs it. Dispose when hidden or inactive.</summary>
    IDisposable AcquireVisualizer();
}

/// <summary>The live effects surface backing the PCM audio player (spec §7.10). In M0 these are the signal-plane values
/// (topology vs param split); the DSP graph that consumes them lands in M2/M3.</summary>
public sealed class AudioEffects : IAudioEffects
{
    private readonly object _visualizerGate = new(); // never acquired on the audio render thread
    private int _visualizerConsumers;
    private long _nextVisualizerEpoch, _visualizerEpoch, _visualizerSource;
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
            _visualizer.Value = VisualizerFrame.Silence;
            Volatile.Write(ref _visualizerEpoch, _visualizerConsumers == 0 ? 0 : ++_nextVisualizerEpoch);
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
