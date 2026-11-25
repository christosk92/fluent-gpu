using System;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The description of one voice's automation (#48 E5-E7), built OFF the render thread and compiled by VoiceAutomation.Build(spec).
// Only the lanes that are set run; an unset lane costs nothing. The engine takes physical units (Hz, dB, frames, 0..1 wet); what
// a Mix curve value means in them is the caller's mapping.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// What one voice's automation does over a window of <see cref="Frames"/> frames starting at mixer frame <see cref="StartFrame"/>
/// (#48 E5-E7): an immutable description, compiled by <see cref="VoiceAutomation.Build(VoiceAutomationSpec)"/> off the render thread.
/// Every table in it is sampled at evenly spaced points across the window (first = the window start, last = its end; normally
/// <see cref="GainEnvelope.TablePoints"/>), holds its first value before the window and its last after it, and is copied by Build,
/// so the caller may reuse its arrays. Unset lanes cost nothing.
/// </summary>
/// <param name="SampleRate">The session's mix rate (<see cref="PcmAudioSession"/>'s format): coefficients are designed at it.</param>
/// <param name="StartFrame">The mixer-domain frame the window starts at.</param>
/// <param name="Frames">The window length in frames (at least 1).</param>
public sealed record VoiceAutomationSpec(int SampleRate, long StartFrame, int Frames)
{
    /// <summary>E2: a low shelf whose gain moves (null = none).</summary>
    public ShelfLaneSpec? LowShelf { get; init; }
    /// <summary>E2: a 12 dB/oct sweep filter whose mode, frequency and Q move (empty = none).</summary>
    public ReadOnlyMemory<SweepPoint> Sweep { get; init; }
    /// <summary>E5: a 3-band EQ (low / mid / high gains over a crossover split) whose gains move (null = none).</summary>
    public EqLaneSpec? Eq { get; init; }
}

/// <summary>E2's low shelf lane: a shelf at <paramref name="Hz"/> (slope Q <see cref="VoiceAutomation.ShelfQ"/>) with its gain in dB
/// per table point (empty = no shelf). Gains are clamped to ±<see cref="BiquadCoeffs.MaxGainDb"/>; a shelf never kills a band (use
/// <see cref="EqLaneSpec"/> for that).</summary>
public sealed record ShelfLaneSpec(float Hz, ReadOnlyMemory<float> GainDb);

/// <summary>
/// E5: a DJ-style 3-band EQ ("isolator"). The voice is split into three bands by two Linkwitz-Riley crossovers at
/// <see cref="LowMidHz"/> and <see cref="MidHighHz"/> (<see cref="Slope"/> dB/oct; the low band is phase-matched with the upper
/// crossover's all-pass, so the three bands at 0 dB sum to a flat all-pass), each band is scaled by its own gain lane, and the bands
/// are summed. Gains are dB per table point: <see cref="KillDb"/> or below (−∞ included) removes the band; above <see cref="MaxDb"/>
/// is clamped. Gains are read per sample with linear interpolation between table points (in linear gain), so a moving gain has no
/// steps and no zipper. An empty table is a band held at 0 dB; all three flat at 0 dB is no EQ at all. A killed band's own path is
/// gone; what remains at its frequencies is the neighbouring bands' crossover skirt (at 48 dB/oct about −48 dB an octave from a
/// crossover and below −80 dB from 1.7 octaves; at 24 dB/oct about −24 dB and −42 dB).
/// <para>Why a split, not a shelf / peak / shelf cascade: a −48 dB shelf or peak is not a band cut (see
/// <see cref="BiquadCoeffs.Design"/>), and a kill cannot be expressed by one.</para>
/// </summary>
/// <param name="LowDb">The low band's gain in dB per table point (below <see cref="LowMidHz"/>).</param>
/// <param name="MidDb">The mid band's gain in dB per table point (between the crossovers).</param>
/// <param name="HighDb">The high band's gain in dB per table point (above <see cref="MidHighHz"/>).</param>
public sealed record EqLaneSpec(ReadOnlyMemory<float> LowDb, ReadOnlyMemory<float> MidDb, ReadOnlyMemory<float> HighDb)
{
    /// <summary>The default low / mid crossover (Hz): where the app's bass swap has always cut (its 200 Hz shelf). The official
    /// client's crossovers are not known; tune them by ear.</summary>
    public const float DefaultLowMidHz = 200f;
    /// <summary>The default mid / high crossover (Hz), a common DJ isolator split.</summary>
    public const float DefaultMidHighHz = 2500f;
    /// <summary>A gain at or below this (dB), or −∞, removes the band (gain 0).</summary>
    public const float KillDb = -120f;
    /// <summary>The largest boost honoured (dB); more is clamped.</summary>
    public const float MaxDb = 12f;

    /// <summary>The low / mid crossover (Hz). Must be below <see cref="MidHighHz"/>.</summary>
    public float LowMidHz { get; init; } = DefaultLowMidHz;
    /// <summary>The mid / high crossover (Hz). Must be above <see cref="LowMidHz"/> and below Nyquist.</summary>
    public float MidHighHz { get; init; } = DefaultMidHighHz;
    /// <summary>Each crossover's slope in dB/oct: 48 (8th-order Linkwitz-Riley, the default: a killed band's residue is below −60 dB
    /// from 1.25 octaves past the crossover) or 24 (4th-order: about half the cost, gentler band edges, a residue of −42 dB 1.7 octaves
    /// out).</summary>
    public int Slope { get; init; } = 48;
}
