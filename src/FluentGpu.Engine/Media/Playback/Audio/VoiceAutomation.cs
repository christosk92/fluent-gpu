using System;
using System.Runtime.CompilerServices;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Per-voice automation (#48 E2, E5): a low shelf whose gain moves, one sweep filter (low-pass, high-pass or off) whose frequency
// and Q move, and a 3-band EQ (a crossover split with a gain lane per band), over a window in the mixer timeline. The control side
// turns curve tables into at most MaxSteps coefficient steps per filter lane, and into interpolated scalar lanes for the EQ gains
// (VoiceAutomation.Build, OFF the render thread); the render thread only steps through them by frame and cross-ramps each step the
// way EqStage cross-ramps a band change. Every voice chain ends with a VoiceAutomationStage that is Bypassed (a value) until
// PcmAudioSession.SetVoiceAutomation arms it. The engine takes Hz, dB and Q; what a curve value means in those units is the
// caller's mapping.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One point of a sweep-filter lane: <see cref="Mode"/> 0 = off (bypass), 1 = low-pass, 2 = high-pass, at
/// <see cref="FreqHz"/> with resonance <see cref="Q"/> (0.707 = Butterworth).</summary>
public readonly record struct SweepPoint(byte Mode, float FreqHz, float Q)
{
    /// <summary>The filter is off at this point.</summary>
    public const byte Bypass = 0;
    /// <summary>A 12 dB/oct low-pass at this point.</summary>
    public const byte LowPass = 1;
    /// <summary>A 12 dB/oct high-pass at this point.</summary>
    public const byte HighPass = 2;
}

/// <summary>
/// An immutable automation for one voice (#48 E2, E5), built OFF the render thread by <see cref="Build(VoiceAutomationSpec)"/>: lanes
/// (low shelf, 3-band EQ, sweep filter) placed in the mixer timeline. Before <see cref="StartFrame"/> a lane holds its first value,
/// after the window its last. Hand it to <see cref="PcmAudioSession.SetVoiceAutomation"/>.
/// </summary>
public sealed class VoiceAutomation
{
    /// <summary>The most coefficient steps one lane carries.</summary>
    public const int MaxSteps = 128;
    /// <summary>The longest cross-ramp between two steps (≈5 ms at 48 kHz, <see cref="EqStage"/>'s declick); a step closer to its
    /// successor ramps over the distance between them.</summary>
    public const int DeclickFrames = 256;
    /// <summary>The low shelf's slope Q.</summary>
    public const float ShelfQ = 0.707f;

    /// <summary>The mixer-domain frame the window starts at.</summary>
    public long StartFrame { get; }
    /// <summary>The window length in frames.</summary>
    public int Frames { get; }
    /// <summary>The sample rate the coefficients were designed at (must be the session's mix rate).</summary>
    public int SampleRate { get; }
    /// <summary>The low shelf's coefficient steps (0 = the lane is off).</summary>
    public int ShelfSteps => Shelf.Count;
    /// <summary>The sweep filter's coefficient steps (0 = the lane is off).</summary>
    public int SweepSteps => Sweep.Count;
    /// <summary>The 3-band EQ lane is on (E5).</summary>
    public bool HasEq => Eq is not null;

    internal readonly Lane Shelf;
    internal readonly Lane Sweep;
    internal readonly EqLane? Eq;

    private VoiceAutomation(int sampleRate, long startFrame, int frames, Lane shelf, Lane sweep, EqLane? eq)
    {
        SampleRate = sampleRate;
        StartFrame = startFrame;
        Frames = frames;
        Shelf = shelf;
        Sweep = sweep;
        Eq = eq;
    }

    /// <summary>One lane's steps, parallel arrays of <see cref="Count"/> entries in frame order.</summary>
    internal sealed class Lane
    {
        public static readonly Lane Empty = new(0);
        public readonly long[] At;              // mixer frame the step takes effect (step 0 applies as soon as the lane is armed)
        public readonly BiquadCoeffs[] Coeffs;
        public readonly bool[] Identity;        // the step is a pass-through (0 dB shelf, sweep off): no filter runs
        public readonly byte[] Type;            // filter kind, so state carries across a ramp only between like filters
        public readonly int[] Ramp;             // cross-ramp length into this step
        public int Count;
        public Lane(int capacity)
        {
            At = new long[capacity]; Coeffs = new BiquadCoeffs[capacity]; Identity = new bool[capacity];
            Type = new byte[capacity]; Ramp = new int[capacity];
        }
        public void Add(long at, in BiquadCoeffs c, bool identity, byte type)
        {
            At[Count] = at; Coeffs[Count] = c; Identity[Count] = identity; Type[Count] = type; Count++;
        }
        public void FinishRamps()
        {
            for (int i = 0; i < Count; i++)
                Ramp[i] = i + 1 < Count ? (int)Math.Clamp(At[i + 1] - At[i], 1, DeclickFrames) : DeclickFrames;
        }
    }

    /// <summary>E5: the 3-band EQ's crossover sections and its three linear gain lanes. A Linkwitz-Riley side is a Butterworth
    /// filter applied twice: LR4 = two 2nd-order sections (Q 1/√2), LR8 = four (Q 0.541, 1.307, twice). The all-pass that phase-matches
    /// the low band is that Butterworth's all-pass at the upper crossover (one or two sections).</summary>
    internal sealed class EqLane
    {
        public readonly BiquadCoeffs[] Lp1, Hp1, Lp2, Hp2, Ap2;
        /// <summary>Biquad states one channel of the split needs.</summary>
        public readonly int Sections;
        public readonly ScalarLane Low, Mid, High;
        public EqLane(BiquadCoeffs[] lp1, BiquadCoeffs[] hp1, BiquadCoeffs[] lp2, BiquadCoeffs[] hp2, BiquadCoeffs[] ap2,
            ScalarLane low, ScalarLane mid, ScalarLane high)
        {
            Lp1 = lp1; Hp1 = hp1; Lp2 = lp2; Hp2 = hp2; Ap2 = ap2; Low = low; Mid = mid; High = high;
            Sections = lp1.Length + hp1.Length + lp2.Length + hp2.Length + ap2.Length;
        }
        /// <summary>Both lanes split at the same crossovers, so one's filter state is valid for the other.</summary>
        public bool SameSplit(EqLane other)
            => Lp1.AsSpan().SequenceEqual(other.Lp1) && Hp1.AsSpan().SequenceEqual(other.Hp1)
            && Lp2.AsSpan().SequenceEqual(other.Lp2) && Hp2.AsSpan().SequenceEqual(other.Hp2);
    }

    /// <summary>
    /// Build an automation for a window of <paramref name="frames"/> frames starting at mixer frame <paramref name="startFrame"/>.
    /// <paramref name="lowShelfDb"/> is the shelf gain in dB at evenly spaced points across the window (first = the window start,
    /// last = its end; empty = no shelf); <paramref name="sweep"/> likewise for the sweep filter (empty = no sweep filter). Up to
    /// <see cref="MaxSteps"/> steps per lane are placed where the value changes fastest (equal steps of change, so a bass swap
    /// over 1/32 of the window gets most of them) and every mode change gets its own step. Allocates: call it off the render thread.
    /// The E2 form of <see cref="Build(VoiceAutomationSpec)"/> (a spec with <see cref="VoiceAutomationSpec.LowShelf"/> and
    /// <see cref="VoiceAutomationSpec.Sweep"/> only).
    /// </summary>
    public static VoiceAutomation Build(int sampleRate, long startFrame, int frames, float lowShelfHz,
        ReadOnlySpan<float> lowShelfDb, ReadOnlySpan<SweepPoint> sweep)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        frames = Math.Max(1, frames);
        var shelf = BuildShelf(sampleRate, startFrame, frames, lowShelfHz, lowShelfDb);
        var sw = BuildSweep(sampleRate, startFrame, frames, sweep);
        return new VoiceAutomation(sampleRate, startFrame, frames, shelf, sw, null);
    }

    /// <summary>
    /// Compile <paramref name="spec"/> (#48 E5): each lane the spec sets, placed over its window. Filter lanes (the low shelf, the
    /// sweep) get up to <see cref="MaxSteps"/> coefficient steps placed where their value changes fastest, as the E2 form does;
    /// gain lanes (the 3-band EQ) keep their table and are read per sample with linear interpolation. A lane that never leaves
    /// neutral (0 dB everywhere, the sweep off) is dropped. Allocates (tables, coefficients): call it off the render thread.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The sample rate is not positive, or the EQ's crossovers are not
    /// <c>0 &lt; LowMidHz &lt; MidHighHz &lt; SampleRate / 2</c>.</exception>
    public static VoiceAutomation Build(VoiceAutomationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        int rate = spec.SampleRate;
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(spec), "SampleRate must be positive.");
        int frames = Math.Max(1, spec.Frames);
        long start = spec.StartFrame;
        var shelf = spec.LowShelf is { } ls ? BuildShelf(rate, start, frames, ls.Hz, ls.GainDb.Span) : Lane.Empty;
        var sweep = BuildSweep(rate, start, frames, spec.Sweep.Span);
        var eq = spec.Eq is { } e ? BuildEq(rate, start, frames, e) : null;
        return new VoiceAutomation(rate, start, frames, shelf, sweep, eq);
    }

    private static long FrameOf(long startFrame, int frames, int index, int points)
        => points <= 1 ? startFrame : startFrame + (long)Math.Round((double)index * frames / (points - 1));

    private static Lane BuildShelf(int sampleRate, long startFrame, int frames, float hz, ReadOnlySpan<float> db)
    {
        if (db.IsEmpty) return Lane.Empty;
        double total = 0;
        for (int i = 1; i < db.Length; i++) total += Math.Abs(db[i] - db[i - 1]);
        double threshold = total / (MaxSteps - 1);
        var lane = new Lane(MaxSteps);
        AddShelf(lane, sampleRate, FrameOf(startFrame, frames, 0, db.Length), hz, db[0]);
        float last = db[0];
        for (int i = 1; i < db.Length && threshold > 0; i++)
        {
            bool final = i == db.Length - 1;
            if (Math.Abs(db[i] - last) < threshold && !(final && db[i] != last)) continue;
            if (lane.Count >= MaxSteps - 1 && !final) continue;   // keep the last slot for the window's end value
            if (lane.Count >= MaxSteps) lane.Count--;
            AddShelf(lane, sampleRate, FrameOf(startFrame, frames, i, db.Length), hz, db[i]);
            last = db[i];
        }
        if (lane.Count == 1 && lane.Identity[0]) return Lane.Empty;   // flat at 0 dB the whole window: the lane is off
        lane.FinishRamps();
        return lane;
    }

    private static void AddShelf(Lane lane, int sampleRate, long at, float hz, float db)
    {
        bool identity = Math.Abs(db) < 0.01f;
        var c = identity ? BiquadCoeffs.Identity
            : BiquadCoeffs.Design(new BiquadBand(BiquadType.LowShelf, hz, ShelfQ, db), sampleRate);
        lane.Add(at, c, identity, (byte)BiquadType.LowShelf);
    }

    private static double SweepDistance(in SweepPoint a, in SweepPoint b)
    {
        if (a.Mode == SweepPoint.Bypass) return 0;
        double f = Math.Abs(Math.Log2(Math.Max(1f, a.FreqHz) / (double)Math.Max(1f, b.FreqHz)));
        double q = Math.Abs(Math.Log2(Math.Max(0.01f, a.Q) / (double)Math.Max(0.01f, b.Q)));
        return f + q;
    }

    internal static Lane BuildSweep(int sampleRate, long startFrame, int frames, ReadOnlySpan<SweepPoint> points)
    {
        if (points.IsEmpty) return Lane.Empty;
        double total = 0;
        for (int i = 1; i < points.Length; i++)
            if (points[i].Mode == points[i - 1].Mode) total += SweepDistance(points[i], points[i - 1]);
        double threshold = total / (MaxSteps - 1);
        var lane = new Lane(MaxSteps);
        AddSweep(lane, sampleRate, FrameOf(startFrame, frames, 0, points.Length), points[0]);
        SweepPoint last = points[0];
        for (int i = 1; i < points.Length; i++)
        {
            bool final = i == points.Length - 1;
            bool modeChange = points[i].Mode != last.Mode;
            double d = modeChange ? double.PositiveInfinity : SweepDistance(points[i], last);
            bool differsAtEnd = final && (modeChange || d > 0);
            if (!modeChange && (threshold <= 0 || d < threshold) && !differsAtEnd) continue;
            if (lane.Count >= MaxSteps - 1 && !final) continue;
            if (lane.Count >= MaxSteps) lane.Count--;
            AddSweep(lane, sampleRate, FrameOf(startFrame, frames, i, points.Length), points[i]);
            last = points[i];
        }
        if (lane.Count == 1 && lane.Identity[0]) return Lane.Empty;
        lane.FinishRamps();
        return lane;
    }

    private static void AddSweep(Lane lane, int sampleRate, long at, in SweepPoint p)
    {
        switch (p.Mode)
        {
            case SweepPoint.LowPass:
                lane.Add(at, BiquadCoeffs.Design(new BiquadBand(BiquadType.LowPass, p.FreqHz, p.Q, 0f), sampleRate), false, (byte)BiquadType.LowPass);
                break;
            case SweepPoint.HighPass:
                lane.Add(at, BiquadCoeffs.Design(new BiquadBand(BiquadType.HighPass, p.FreqHz, p.Q, 0f), sampleRate), false, (byte)BiquadType.HighPass);
                break;
            default:
                lane.Add(at, BiquadCoeffs.Identity, true, 0);
                break;
        }
    }

    // ── E5: the 3-band EQ ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The most biquad states one channel of the 3-band split needs (LR8: 4 × 4 crossover sections + 2 all-pass).</summary>
    internal const int MaxEqSections = 18;
    private static readonly float[] Lr4Q = [0.70710678f];               // the 2nd-order Butterworth
    private static readonly float[] Lr8Q = [0.54119610f, 1.30656296f];  // the 4th-order Butterworth's two sections

    private static EqLane? BuildEq(int rate, long start, int frames, EqLaneSpec spec)
    {
        float f1 = spec.LowMidHz, f2 = spec.MidHighHz;
        if (!(f1 > 0f) || !(f2 > f1) || !(f2 < rate * 0.5f))
            throw new ArgumentOutOfRangeException(nameof(spec), $"EQ crossovers must satisfy 0 < LowMidHz ({f1}) < MidHighHz ({f2}) < SampleRate / 2.");
        if (spec.Slope is not (24 or 48)) throw new ArgumentOutOfRangeException(nameof(spec), $"EQ slope must be 24 or 48 dB/oct, not {spec.Slope}.");
        var low = GainLane(spec.LowDb.Span, start, frames);
        var mid = GainLane(spec.MidDb.Span, start, frames);
        var high = GainLane(spec.HighDb.Span, start, frames);
        if (low.IsConstant(1f) && mid.IsConstant(1f) && high.IsConstant(1f)) return null;   // flat at 0 dB: the lane is off
        var qs = spec.Slope == 48 ? Lr8Q : Lr4Q;
        return new EqLane(Side(BiquadType.LowPass, f1, qs, rate), Side(BiquadType.HighPass, f1, qs, rate),
            Side(BiquadType.LowPass, f2, qs, rate), Side(BiquadType.HighPass, f2, qs, rate), AllPasses(f2, qs, rate), low, mid, high);
    }

    /// <summary>One Linkwitz-Riley side: the Butterworth sections, twice.</summary>
    private static BiquadCoeffs[] Side(BiquadType type, float hz, float[] qs, int rate)
    {
        var s = new BiquadCoeffs[2 * qs.Length];
        for (int i = 0; i < s.Length; i++) s[i] = BiquadCoeffs.Design(new BiquadBand(type, hz, qs[i % qs.Length], 0f), rate);
        return s;
    }

    private static BiquadCoeffs[] AllPasses(float hz, float[] qs, int rate)
    {
        var s = new BiquadCoeffs[qs.Length];
        for (int i = 0; i < s.Length; i++) s[i] = BiquadCoeffs.AllPass(hz, qs[i], rate);
        return s;
    }

    private static ScalarLane GainLane(ReadOnlySpan<float> db, long start, int frames)
    {
        if (db.IsEmpty) return new ScalarLane([1f], start, frames);
        var v = new float[db.Length];
        for (int i = 0; i < v.Length; i++) v[i] = EqGain(db[i]);
        return new ScalarLane(v, start, frames);
    }

    /// <summary>An EQ band's dB to its linear gain: <see cref="EqLaneSpec.KillDb"/> or below (−∞ included) is 0, above
    /// <see cref="EqLaneSpec.MaxDb"/> is clamped, NaN is 0 dB.</summary>
    internal static float EqGain(float db)
        => float.IsNaN(db) ? 1f : db <= EqLaneSpec.KillDb ? 0f : MathF.Pow(10f, MathF.Min(db, EqLaneSpec.MaxDb) / 20f);
}

/// <summary>
/// A scalar automation lane (#48 E5-E7): a value per table point, evenly spaced across a window of the mixer timeline, read with
/// linear interpolation; the first value holds before the window, the last after it. Immutable once built; reads are alloc-free.
/// </summary>
internal sealed class ScalarLane
{
    private readonly float[] _v;
    private readonly long _start;
    private readonly int _frames;
    private readonly double _perFrame;   // table points per frame

    public ScalarLane(float[] values, long start, int frames)
    {
        _v = values;
        _start = start;
        _frames = Math.Max(1, frames);
        _perFrame = values.Length > 1 ? (values.Length - 1) / (double)_frames : 0.0;
    }

    /// <summary>Every point equals <paramref name="value"/>.</summary>
    public bool IsConstant(float value)
    {
        foreach (float x in _v) if (x != value) return false;
        return true;
    }

    /// <summary>The value at mixer frame <paramref name="frame"/>.</summary>
    public float At(long frame)
    {
        long off = frame - _start;
        if (_v.Length == 1 || off <= 0) return _v[0];
        if (off >= _frames) return _v[^1];
        double x = off * _perFrame;
        int k = (int)x;
        float a = _v[k];
        return a + (_v[k + 1] - a) * (float)(x - k);
    }

    /// <summary>The values for <c>dst.Length</c> frames from mixer frame <paramref name="frame"/>. Alloc-free.</summary>
    public void Fill(Span<float> dst, long frame)
    {
        if (_v.Length == 1) { dst.Fill(_v[0]); return; }
        long off = frame - _start;
        int n = dst.Length, i = 0;
        float first = _v[0], last = _v[^1];
        for (; i < n && off + i <= 0; i++) dst[i] = first;
        for (; i < n && off + i < _frames; i++)
        {
            double x = (off + i) * _perFrame;
            int k = (int)x;
            float a = _v[k];
            dst[i] = a + (_v[k + 1] - a) * (float)(x - k);
        }
        for (; i < n; i++) dst[i] = last;
    }

    /// <summary>The lane is 0 over the <paramref name="count"/> frames from <paramref name="frame"/>.</summary>
    public bool ZeroOver(long frame, int count)
    {
        long a = frame - _start, b = a + Math.Max(1, count) - 1;
        if (b <= 0 || _v.Length == 1) return _v[0] == 0f;
        if (a >= _frames) return _v[^1] == 0f;
        int k0 = a <= 0 ? 0 : (int)(a * _perFrame);
        int k1 = b >= _frames ? _v.Length - 1 : Math.Min(_v.Length - 1, (int)(b * _perFrame) + 1);
        for (int k = k0; k <= k1; k++) if (_v[k] != 0f) return false;
        return true;
    }
}

/// <summary>
/// The last stage of every voice chain (#48 E2, E5): runs a <see cref="VoiceAutomation"/>'s lanes (low shelf, 3-band EQ, then the
/// sweep filter) in place. <see cref="IDspStage.Bypassed"/> until armed — a bypass is a value, so an unarmed voice costs one branch
/// per block. Armed from bypass, the shelf's and the sweep's first values apply at once (arm it before the voice sounds, or with a
/// window whose first values are neutral, as a mix-out's are); the 3-band EQ ramps in from the dry signal over
/// <see cref="VoiceAutomation.DeclickFrames"/> unless the voice has not sounded yet (its split changes the phase, so switching it
/// in at once would click). Re-armed while running, or disarmed (<c>null</c>), the stage cross-ramps from what is audible over
/// <see cref="VoiceAutomation.DeclickFrames"/> and, once neutral, bypasses again. Render thread only; alloc-free.
/// </summary>
public sealed class VoiceAutomationStage : IDspStage
{
    /// <summary>Gain lanes are read in chunks of this many frames into the stage's scratch.</summary>
    internal const int Chunk = 64;

    private readonly int _channels;
    private readonly LaneState _shelf;
    private readonly LaneState _sweep;
    private readonly IsolatorState _eq;
    private VoiceAutomation? _armed;
    private bool _hasRun;                        // the stage has processed a block (the voice may be sounding)

    /// <summary>A stage for <paramref name="channels"/> channels (further channels pass through). Allocates its state once.</summary>
    public VoiceAutomationStage(int channels)
    {
        _channels = Math.Max(1, channels);
        _shelf = new LaneState(_channels);
        _sweep = new LaneState(_channels);
        _eq = new IsolatorState(_channels);
    }

    /// <inheritdoc/>
    public int LatencySamples => 0;
    /// <inheritdoc/>
    public bool Bypassed { get; set; } = true;
    /// <summary>The automation currently armed (null while bypassed or ramping back to neutral).</summary>
    public VoiceAutomation? Armed => _armed;

    /// <summary>RENDER thread (a mixer command), alloc-free: arm <paramref name="automation"/>, or ramp back to neutral and bypass
    /// with <c>null</c>.</summary>
    public void Arm(VoiceAutomation? automation)
    {
        if (automation is null)
        {
            _armed = null;
            if (Bypassed) return;
            _shelf.Target(VoiceAutomation.Lane.Empty, ramp: true);
            _sweep.Target(VoiceAutomation.Lane.Empty, ramp: true);
            _eq.Target(null, ramp: true);
            return;
        }
        bool fromBypass = Bypassed;
        if (fromBypass) { _shelf.Reset(); _sweep.Reset(); _eq.Reset(); }
        _armed = automation;
        _shelf.Target(automation.Shelf, ramp: !fromBypass);
        _sweep.Target(automation.Sweep, ramp: !fromBypass);
        _eq.Target(automation.Eq, ramp: _hasRun);
        Bypassed = false;
    }

    /// <inheritdoc/>
    public int Process(ReadOnlySpan<float> src, Span<float> dst, int frames, in BlockCtx ctx)
    {
        int n = frames * ctx.Channels;
        if (!src.Overlaps(dst, out int offset) || offset != 0) src[..n].CopyTo(dst);
        _hasRun = true;
        if (Bypassed) return frames;
        int ch = Math.Min(ctx.Channels, _channels);
        _shelf.Run(dst, frames, ctx.Channels, ch, ctx.StartFrame);
        _eq.Run(dst, frames, ctx.Channels, ch, ctx.StartFrame);
        _sweep.Run(dst, frames, ctx.Channels, ch, ctx.StartFrame);
        if (_armed is null && _shelf.Neutral && _sweep.Neutral && _eq.Neutral)
        {
            Bypassed = true;   // the disarm ramp has settled
            _shelf.Reset();
            _sweep.Reset();
            _eq.Reset();
        }
        return frames;
    }

    /// <summary>One lane's render-thread state: the audible (active) coefficients, the ones being ramped in (pending), and where
    /// the lane is in its step list.</summary>
    private sealed class LaneState
    {
        private readonly BiquadState[] _stActive, _stPending;
        private VoiceAutomation.Lane _lane = VoiceAutomation.Lane.Empty;
        private int _next;                       // the next step to adopt
        private BiquadCoeffs _active = BiquadCoeffs.Identity, _pending = BiquadCoeffs.Identity;
        private bool _activeId = true, _pendingId = true;
        private byte _activeType, _pendingType;
        private int _rampRemaining, _rampLength;
        private bool _rampFirst;                 // the first step of a re-arm or a disarm ramps even though step 0 may say 0

        public LaneState(int channels)
        {
            _stActive = new BiquadState[channels];
            _stPending = new BiquadState[channels];
        }

        /// <summary>No more steps, not ramping, and passing audio through.</summary>
        public bool Neutral => _next >= _lane.Count && _rampRemaining == 0 && _activeId;

        public void Reset()
        {
            Array.Clear(_stActive);
            Array.Clear(_stPending);
            _active = _pending = BiquadCoeffs.Identity;
            _activeId = _pendingId = true;
            _activeType = _pendingType = 0;
            _rampRemaining = 0;
            _lane = VoiceAutomation.Lane.Empty;
            _next = 0;
        }

        /// <summary>Follow <paramref name="lane"/> from its first step; an empty lane is "go neutral".</summary>
        public void Target(VoiceAutomation.Lane lane, bool ramp)
        {
            _lane = lane;
            _next = 0;
            _rampFirst = ramp;
            if (lane.Count == 0) Adopt(BiquadCoeffs.Identity, true, 0, ramp ? VoiceAutomation.DeclickFrames : 0);
        }

        private void Adopt(in BiquadCoeffs c, bool identity, byte type, int ramp)
        {
            if (_rampRemaining > 0) Commit();
            if (ramp <= 0)
            {
                if (identity || type != _activeType || _activeId) Array.Clear(_stActive);
                _active = c; _activeId = identity; _activeType = type;
                return;
            }
            bool carry = !_activeId && !identity && type == _activeType;
            if (carry) Array.Copy(_stActive, _stPending, _stActive.Length);
            else Array.Clear(_stPending);
            _pending = c; _pendingId = identity; _pendingType = type;
            _rampRemaining = _rampLength = ramp;
        }

        private void Commit()
        {
            _active = _pending; _activeId = _pendingId; _activeType = _pendingType;
            Array.Copy(_stPending, _stActive, _stActive.Length);
            _rampRemaining = 0;
        }

        public void Run(Span<float> buf, int frames, int stride, int ch, long startFrame)
        {
            var lane = _lane;
            for (int f = 0; f < frames; f++)
            {
                if (_next < lane.Count && (_next == 0 || startFrame + f >= lane.At[_next]))
                {
                    long frame = startFrame + f;
                    int k = _next;
                    while (k + 1 < lane.Count && frame >= lane.At[k + 1]) k++;   // armed late: jump to the step that is current now
                    int ramp = k == 0 ? (_rampFirst ? VoiceAutomation.DeclickFrames : 0) : lane.Ramp[k];
                    Adopt(lane.Coeffs[k], lane.Identity[k], lane.Type[k], ramp);
                    _next = k + 1;
                }
                if (_activeId && _rampRemaining == 0)
                {
                    // Steady pass-through: skip to the next step (or the end of the block) in one go.
                    if (_next >= lane.Count) return;
                    long until = lane.At[_next] - startFrame;
                    if (until > frames) return;
                    f = (int)Math.Max(f, until - 1);
                    continue;
                }
                int b = f * stride;
                if (_rampRemaining > 0)
                {
                    float t = 1f - (_rampRemaining - 1) / (float)_rampLength;   // 0 → 1 across the ramp
                    for (int c = 0; c < ch; c++)
                    {
                        float x = buf[b + c];
                        float ya = _activeId ? x : _stActive[c].Process(x, in _active);
                        float yp = _pendingId ? x : _stPending[c].Process(x, in _pending);
                        buf[b + c] = ya * (1f - t) + yp * t;
                    }
                    if (--_rampRemaining == 0) Commit();
                }
                else
                {
                    for (int c = 0; c < ch; c++) buf[b + c] = _stActive[c].Process(buf[b + c], in _active);
                }
            }
        }
    }

    /// <summary>
    /// E5's render-thread state: the 3-band split of the audible (active) EQ lane and of the one being ramped in (pending), each
    /// with its own filter state; a null lane is the dry signal. Per channel, in order: the low-pass and the high-pass at the
    /// low / mid crossover, the low-pass and the high-pass at the mid / high crossover over the upper band, and the upper crossover's
    /// all-pass on the low band (so the three bands sum flat). Gains are read per chunk into the state's scratch.
    /// </summary>
    private sealed class IsolatorState
    {
        private const int Sections = VoiceAutomation.MaxEqSections;   // state slots per channel (an LR4 lane uses the first 9)
        private readonly BiquadState[] _sa, _sp;
        private readonly float[] _ga = new float[3 * Chunk], _gp = new float[3 * Chunk];
        private VoiceAutomation.EqLane? _a, _p;
        private int _ramp, _rampLength;

        public IsolatorState(int channels)
        {
            _sa = new BiquadState[channels * Sections];
            _sp = new BiquadState[channels * Sections];
        }

        /// <summary>Passing audio through and not ramping.</summary>
        public bool Neutral => _a is null && _ramp == 0;

        public void Reset()
        {
            Array.Clear(_sa);
            Array.Clear(_sp);
            _a = _p = null;
            _ramp = 0;
        }

        /// <summary>Follow <paramref name="lane"/> (null = the dry signal), cross-ramped from what is audible when
        /// <paramref name="ramp"/>, else at once.</summary>
        public void Target(VoiceAutomation.EqLane? lane, bool ramp)
        {
            if (!ramp)
            {
                Array.Clear(_sa);
                _a = lane; _p = null; _ramp = 0;
                return;
            }
            if (_ramp > 0) Commit();
            if (ReferenceEquals(lane, _a)) return;
            bool carry = lane is not null && _a is not null && lane.SameSplit(_a);
            if (carry) Array.Copy(_sa, _sp, _sa.Length);
            else Array.Clear(_sp);
            _p = lane;
            _ramp = _rampLength = VoiceAutomation.DeclickFrames;
        }

        private void Commit()
        {
            _a = _p; _p = null;
            Array.Copy(_sp, _sa, _sa.Length);
            _ramp = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Split(BiquadState[] st, int o, VoiceAutomation.EqLane l, float x, float gl, float gm, float gh)
        {
            BiquadCoeffs[] lp1 = l.Lp1, hp1 = l.Hp1, lp2 = l.Lp2, hp2 = l.Hp2, ap2 = l.Ap2;
            float lo = x, rest = x;
            for (int i = 0; i < lp1.Length; i++) lo = st[o++].Process(lo, in lp1[i]);
            for (int i = 0; i < hp1.Length; i++) rest = st[o++].Process(rest, in hp1[i]);
            float mid = rest, hi = rest;
            for (int i = 0; i < lp2.Length; i++) mid = st[o++].Process(mid, in lp2[i]);
            for (int i = 0; i < hp2.Length; i++) hi = st[o++].Process(hi, in hp2[i]);
            for (int i = 0; i < ap2.Length; i++) lo = st[o++].Process(lo, in ap2[i]);
            return gl * lo + gm * mid + gh * hi;
        }

        private static void Gains(VoiceAutomation.EqLane l, float[] g, int n, long frame)
        {
            l.Low.Fill(g.AsSpan(0, n), frame);
            l.Mid.Fill(g.AsSpan(Chunk, n), frame);
            l.High.Fill(g.AsSpan(2 * Chunk, n), frame);
        }

        public void Run(Span<float> buf, int frames, int stride, int ch, long startFrame)
        {
            for (int off = 0; off < frames;)
            {
                if (_a is null && _ramp == 0) return;
                int n = Math.Min(Chunk, frames - off);
                if (_ramp > 0) n = Math.Min(n, _ramp);   // a ramp ends on a chunk edge, so a chunk reads one lane set
                long frame = startFrame + off;
                var a = _a;
                var p = _p;
                if (a is not null) Gains(a, _ga, n, frame);
                if (_ramp == 0)
                {
                    for (int f = 0; f < n; f++)
                    {
                        int b = (off + f) * stride;
                        float gl = _ga[f], gm = _ga[Chunk + f], gh = _ga[2 * Chunk + f];
                        for (int c = 0; c < ch; c++) buf[b + c] = Split(_sa, c * Sections, a!, buf[b + c], gl, gm, gh);
                    }
                }
                else
                {
                    if (p is not null) Gains(p, _gp, n, frame);
                    for (int f = 0; f < n; f++)
                    {
                        int b = (off + f) * stride;
                        float t = 1f - (_ramp - 1) / (float)_rampLength;   // 0 → 1 across the ramp
                        for (int c = 0; c < ch; c++)
                        {
                            float x = buf[b + c];
                            float ya = a is null ? x : Split(_sa, c * Sections, a, x, _ga[f], _ga[Chunk + f], _ga[2 * Chunk + f]);
                            float yp = p is null ? x : Split(_sp, c * Sections, p, x, _gp[f], _gp[Chunk + f], _gp[2 * Chunk + f]);
                            buf[b + c] = ya + (yp - ya) * t;
                        }
                        _ramp--;
                    }
                    if (_ramp == 0) Commit();
                }
                off += n;
            }
        }
    }
}
