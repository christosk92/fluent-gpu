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
    /// <summary>E6: a noise generator mixed into the voice, its level and colour moving (null = none).</summary>
    public NoiseLaneSpec? Noise { get; init; }
    /// <summary>E7: an echo (a delay line with feedback) whose dry/wet moves (null = none). Runs after the voice's gain envelope;
    /// see <see cref="DelayLaneSpec"/>.</summary>
    public DelayLaneSpec? Echo { get; init; }
    /// <summary>E7: a delay (a second delay line, by default a single repeat) whose dry/wet moves (null = none). Runs after the
    /// echo.</summary>
    public DelayLaneSpec? Delay { get; init; }
    /// <summary>E7: a reverb whose dry/wet moves (null = none). Runs after the echo and the delay; see
    /// <see cref="ReverbLaneSpec"/>.</summary>
    public ReverbLaneSpec? Reverb { get; init; }
    /// <summary>The voice's channel count (the mix layout is stereo): sizes the effects' delay lines. Channels past it pass the
    /// effects by dry.</summary>
    public int Channels { get; init; } = 2;
}

/// <summary>How an effect's 0..1 dry/wet (<c>w</c>) splits into the dry gain and the send into the effect (#48 E7). Applied to the
/// table when the automation is built, so a law costs nothing on the render thread. At <c>w = 0</c> every law is dry only.</summary>
public enum DryWetLaw : byte
{
    /// <summary>Constant power: dry = cos(w·π/2), send = sin(w·π/2) (the default; dry and wet at −3 dB each at 0.5).</summary>
    EqualPower,
    /// <summary>Linear: dry = 1 − w, send = w.</summary>
    Linear,
    /// <summary>An effect send: dry = 1 throughout, send = w (the effect is laid over the voice).</summary>
    Send,
    /// <summary>A DJ mixer's FX knob: dry = min(1, 2·(1 − w)), send = min(1, 2·w) (both full at 0.5, wet only at 1).</summary>
    DjKnob,
}

/// <summary>
/// E7: an echo or a delay — one delay line per channel, <see cref="DelayFrames"/> long, with <see cref="Feedback"/> and an
/// optional low-pass in the feedback path. The caller computes the delay from the tempo (½, ¾ or 1 beat:
/// <c>frames = beats × 60 × sampleRate / bpm</c>). Echo and delay differ only in feedback: an echo repeats and decays
/// (<see cref="DefaultEchoFeedback"/>, −6 dB a repeat), a delay plays the voice once more (<see cref="DefaultDelayFeedback"/>).
/// <para>The effect runs AFTER the voice's gain envelope, fed through the send (trails): what the send let in keeps repeating
/// after the wet lane returns to 0 and after the voice's envelope reaches 0 — an "echo out". The voice stays in the mixer, reading
/// nothing from its source, until the tail is below −80 dBFS (or the voice is removed). The delay line is allocated when the
/// automation is built (<c>DelayFrames × Channels</c> floats) and freed with the voice.</para>
/// </summary>
/// <param name="DelayFrames">The delay in frames at the session rate (1 to <see cref="MaxDelaySeconds"/> seconds' worth; clamped).</param>
/// <param name="Wet">The dry/wet per table point, 0..1 (clamped), split by <see cref="Law"/>.</param>
public sealed record DelayLaneSpec(int DelayFrames, ReadOnlyMemory<float> Wet)
{
    /// <summary>An echo's feedback: each repeat at half the level before (−6 dB), 13 repeats to −80 dB.</summary>
    public const float DefaultEchoFeedback = 0.5f;
    /// <summary>A delay's feedback: one repeat.</summary>
    public const float DefaultDelayFeedback = 0f;
    /// <summary>The largest feedback honoured (−0.45 dB a repeat); more is clamped, so a tail always ends.</summary>
    public const float MaxFeedback = 0.95f;
    /// <summary>The longest delay line, in seconds.</summary>
    public const int MaxDelaySeconds = 8;

    /// <summary>The level of each repeat relative to the one before, 0..<see cref="MaxFeedback"/> (default
    /// <see cref="DefaultEchoFeedback"/>; use <see cref="DefaultDelayFeedback"/> for a single repeat).</summary>
    public float Feedback { get; init; } = DefaultEchoFeedback;
    /// <summary>A one-pole low-pass in the feedback path (Hz): each repeat darker than the one before, as a tape or DJ echo. 0 (the
    /// default) = none, every repeat a copy.</summary>
    public float HighCutHz { get; init; }
    /// <summary>How <see cref="Wet"/> splits into dry and send.</summary>
    public DryWetLaw Law { get; init; } = DryWetLaw.EqualPower;
}

/// <summary>
/// E7: a small stereo reverb — an 8-line feedback delay network (lines of 30 to 58 ms, mutually prime, a Householder mixing
/// matrix, per-line decay gains set from <see cref="DecaySeconds"/> so the tail falls 60 dB in that time, a one-pole damping
/// low-pass per line) behind a high-pass on the send (<see cref="LowCutHz"/>) and two Schroeder all-pass diffusers per channel. The wet output is normalised to about the input's level
/// for a steady noise input. Like <see cref="DelayLaneSpec"/> it runs after the voice's gain envelope, fed through the send, and
/// rings out after the voice ends until it is below −80 dBFS. About 80 KB of lines at 48 kHz, allocated when the automation is
/// built.
/// </summary>
/// <param name="DecaySeconds">RT60: the time the tail takes to fall 60 dB (0.1 to <see cref="MaxDecaySeconds"/>; clamped). With
/// damping, the highs fall faster than this.</param>
/// <param name="Wet">The dry/wet per table point, 0..1 (clamped), split by <see cref="Law"/>.</param>
public sealed record ReverbLaneSpec(float DecaySeconds, ReadOnlyMemory<float> Wet)
{
    /// <summary>The longest decay honoured (s).</summary>
    public const float MaxDecaySeconds = 20f;
    /// <summary>The default damping corner (Hz).</summary>
    public const float DefaultHighCutHz = 6000f;
    /// <summary>The default low cut on the send (Hz).</summary>
    public const float DefaultLowCutHz = 80f;
    /// <summary>The lowest low cut honoured (Hz): the network has a mode at DC (every line in phase passes the mixing matrix at
    /// the loop gain), so without one a DC offset or sub-bass comes out up to 1 / (1 − g) louder — about +20 dB at a 2 s decay.</summary>
    public const float MinLowCutHz = 20f;

    /// <summary>The damping low-pass in each line's feedback (Hz; 0 = none, every frequency decays in <see cref="DecaySeconds"/>).</summary>
    public float HighCutHz { get; init; } = DefaultHighCutHz;
    /// <summary>A 12 dB/oct high-pass on what the send feeds the reverb (Hz, at least <see cref="MinLowCutHz"/>): keeps the bass out of
    /// the tail, and DC out of the network.</summary>
    public float LowCutHz { get; init; } = DefaultLowCutHz;
    /// <summary>How <see cref="Wet"/> splits into dry and send.</summary>
    public DryWetLaw Law { get; init; } = DryWetLaw.EqualPower;
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

/// <summary>
/// E6: white noise mixed into the voice. It is added after the voice's filter lanes (so the voice's EQ and sweep never filter it)
/// and before the voice's gain envelope and effects (so the envelope fades it with the voice and an echo or reverb carries it).
/// <see cref="Colour"/> sweeps a filter over the noise (a 12 dB/oct low-pass or high-pass, or off = white), stepped and
/// cross-ramped like the voice's sweep lane; the level is read per sample with linear interpolation. The generator is a
/// deterministic xorshift per channel, seeded from <see cref="Seed"/> (the channels are decorrelated; the same spec makes the same
/// noise), and allocates nothing while it runs.
/// </summary>
/// <param name="LevelDb">The noise level per table point, in dBFS RMS of the white noise before the colour filter (so a narrow
/// colour sounds quieter than white at the same level). <see cref="OffDb"/> or below (−∞ included) is silence; above 0 dB is
/// clamped. All points silent is no noise at all.</param>
public sealed record NoiseLaneSpec(ReadOnlyMemory<float> LevelDb)
{
    /// <summary>A level at or below this (dB), or −∞, is silence.</summary>
    public const float OffDb = -120f;
    /// <summary>The seed a spec carries unless it sets one.</summary>
    public const uint DefaultSeed = 0x2545F491u;

    /// <summary>The colour filter per table point (empty = white noise throughout). A point's <see cref="SweepPoint.Mode"/>
    /// <see cref="SweepPoint.Bypass"/> is white.</summary>
    public ReadOnlyMemory<SweepPoint> Colour { get; init; }
    /// <summary>The generator's seed (each channel derives its own from it).</summary>
    public uint Seed { get; init; } = DefaultSeed;
}
