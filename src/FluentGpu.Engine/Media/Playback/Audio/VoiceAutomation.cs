using System;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Per-voice automation (#48 E2): a low shelf whose gain moves and one sweep filter (low-pass, high-pass or off) whose frequency
// and Q move, over a window in the mixer timeline. The control side turns curve tables into at most MaxSteps coefficient steps
// per lane (VoiceAutomation.Build, OFF the render thread); the render thread only steps through them by frame and cross-ramps
// each step the way EqStage cross-ramps a band change. Every voice chain ends with a VoiceAutomationStage that is Bypassed
// (a value) until PcmAudioSession.SetVoiceAutomation arms it. The engine takes Hz, dB and Q; what a curve value means in those
// units is the caller's mapping.
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
/// An immutable automation for one voice (#48 E2), built OFF the render thread by <see cref="Build"/>: two lanes (low shelf,
/// sweep filter) of precomputed coefficient steps placed in the mixer timeline. Before <see cref="StartFrame"/> a lane holds its
/// first value, after the window its last. Hand it to <see cref="PcmAudioSession.SetVoiceAutomation"/>.
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

    internal readonly Lane Shelf;
    internal readonly Lane Sweep;

    private VoiceAutomation(int sampleRate, long startFrame, int frames, Lane shelf, Lane sweep)
    {
        SampleRate = sampleRate;
        StartFrame = startFrame;
        Frames = frames;
        Shelf = shelf;
        Sweep = sweep;
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

    /// <summary>
    /// Build an automation for a window of <paramref name="frames"/> frames starting at mixer frame <paramref name="startFrame"/>.
    /// <paramref name="lowShelfDb"/> is the shelf gain in dB at evenly spaced points across the window (first = the window start,
    /// last = its end; empty = no shelf); <paramref name="sweep"/> likewise for the sweep filter (empty = no sweep filter). Up to
    /// <see cref="MaxSteps"/> steps per lane are placed where the value changes fastest (equal steps of change, so a bass swap
    /// over 1/32 of the window gets most of them) and every mode change gets its own step. Allocates: call it off the render thread.
    /// </summary>
    public static VoiceAutomation Build(int sampleRate, long startFrame, int frames, float lowShelfHz,
        ReadOnlySpan<float> lowShelfDb, ReadOnlySpan<SweepPoint> sweep)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        frames = Math.Max(1, frames);
        var shelf = BuildShelf(sampleRate, startFrame, frames, lowShelfHz, lowShelfDb);
        var sw = BuildSweep(sampleRate, startFrame, frames, sweep);
        return new VoiceAutomation(sampleRate, startFrame, frames, shelf, sw);
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

    private static Lane BuildSweep(int sampleRate, long startFrame, int frames, ReadOnlySpan<SweepPoint> points)
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
}

/// <summary>
/// The last stage of every voice chain (#48 E2): runs a <see cref="VoiceAutomation"/>'s two lanes (low shelf, then the sweep
/// filter) in place. <see cref="IDspStage.Bypassed"/> until armed — a bypass is a value, so an unarmed voice costs one branch per
/// block. Armed from bypass, the automation's first values apply at once (arm it before the voice sounds, or with a window whose
/// first values are neutral, as a mix-out's are); re-armed while running, or disarmed (<c>null</c>), the stage cross-ramps from
/// what is audible over <see cref="VoiceAutomation.DeclickFrames"/> and, once neutral, bypasses again. Render thread only;
/// alloc-free.
/// </summary>
public sealed class VoiceAutomationStage : IDspStage
{
    private readonly int _channels;
    private readonly LaneState _shelf;
    private readonly LaneState _sweep;
    private VoiceAutomation? _armed;

    /// <summary>A stage for <paramref name="channels"/> channels (further channels pass through). Allocates its state once.</summary>
    public VoiceAutomationStage(int channels)
    {
        _channels = Math.Max(1, channels);
        _shelf = new LaneState(_channels);
        _sweep = new LaneState(_channels);
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
            return;
        }
        bool fromBypass = Bypassed;
        if (fromBypass) { _shelf.Reset(); _sweep.Reset(); }
        _armed = automation;
        _shelf.Target(automation.Shelf, ramp: !fromBypass);
        _sweep.Target(automation.Sweep, ramp: !fromBypass);
        Bypassed = false;
    }

    /// <inheritdoc/>
    public int Process(ReadOnlySpan<float> src, Span<float> dst, int frames, in BlockCtx ctx)
    {
        int n = frames * ctx.Channels;
        if (!src.Overlaps(dst, out int offset) || offset != 0) src[..n].CopyTo(dst);
        if (Bypassed) return frames;
        int ch = Math.Min(ctx.Channels, _channels);
        _shelf.Run(dst, frames, ctx.Channels, ch, ctx.StartFrame);
        _sweep.Run(dst, frames, ctx.Channels, ch, ctx.StartFrame);
        if (_armed is null && _shelf.Neutral && _sweep.Neutral)
        {
            Bypassed = true;   // the disarm ramp has settled
            _shelf.Reset();
            _sweep.Reset();
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
}
