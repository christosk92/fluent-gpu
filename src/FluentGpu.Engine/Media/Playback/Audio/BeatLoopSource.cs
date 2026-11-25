using System;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>#48 E8: the type of a loop block, the numbering of the Mix recipe's block message (reference §2, §3.2). Only
/// <see cref="Loop"/> renders; <see cref="Spinback"/> and <see cref="VinylStop"/> are reserved for the two other block types
/// the reference names (which looping styles use them is open): a schedule carrying one is refused until they are built.</summary>
public enum LoopBlockKind : byte
{
    /// <summary>Repeat a region of the voice's own audio.</summary>
    Loop = 1,
    /// <summary>Reserved (not rendered).</summary>
    Spinback = 2,
    /// <summary>Reserved (not rendered).</summary>
    VinylStop = 3,
}

/// <summary>#48 E8: what a voice does when its loop schedule's last block ends.</summary>
public enum LoopEnd : byte
{
    /// <summary>Continue with the original audio where it would be had it never looped (the original runs on underneath the loop).</summary>
    Continue = 0,
    /// <summary>Stop the voice (loop-out: the voice's envelope has faded by then): a seam to silence, then the voice is exhausted.</summary>
    Stop = 1,
}

/// <summary>
/// #48 E8: one loop block, in the voice's SOURCE frames (its content position, the domain <see cref="IAudioSource.PositionFrames"/>
/// counts in). The first pass over <c>[StartFrame, StartFrame + LengthFrames)</c> is the original audio; at its end the region
/// repeats, <see cref="Repeats"/> times or until the original reaches <see cref="UntilFrame"/>. A halving block plays each repeat half
/// as long as the one before (the first repeat half the block's length), down to <see cref="FloorFrames"/>, from the same start.
/// The caller computes the lengths from the beat grid (1, 2, 4, 8 or 16 beats).
/// </summary>
public readonly record struct LoopBlock
{
    /// <summary>The block type (only <see cref="LoopBlockKind.Loop"/> renders).</summary>
    public LoopBlockKind Kind { get; init; }
    /// <summary>The loop region's first source frame.</summary>
    public long StartFrame { get; init; }
    /// <summary>The loop length (the first pass, and every repeat of a non-halving block).</summary>
    public int LengthFrames { get; init; }
    /// <summary>Repeats after the first pass (0 = until <see cref="UntilFrame"/>).</summary>
    public int Repeats { get; init; }
    /// <summary>The source frame the block ends at (-1 = after <see cref="Repeats"/>); with both, the earlier wins.</summary>
    public long UntilFrame { get; init; }
    /// <summary>Each repeat half as long as the one before, down to <see cref="FloorFrames"/>.</summary>
    public bool Halving { get; init; }
    /// <summary>The shortest repeat of a halving block.</summary>
    public int FloorFrames { get; init; }

    /// <summary>A loop of <paramref name="lengthFrames"/> from <paramref name="startFrame"/>, repeated <paramref name="repeats"/> times.</summary>
    public static LoopBlock Repeat(long startFrame, int lengthFrames, int repeats)
        => new() { Kind = LoopBlockKind.Loop, StartFrame = startFrame, LengthFrames = lengthFrames, Repeats = repeats, UntilFrame = -1 };

    /// <summary>A loop of <paramref name="lengthFrames"/> from <paramref name="startFrame"/>, repeated until the original reaches <paramref name="untilFrame"/>.</summary>
    public static LoopBlock Until(long startFrame, int lengthFrames, long untilFrame)
        => new() { Kind = LoopBlockKind.Loop, StartFrame = startFrame, LengthFrames = lengthFrames, UntilFrame = untilFrame };

    /// <summary>A halving loop: repeats of length/2, length/4, … down to <paramref name="floorFrames"/>, <paramref name="repeats"/> of them.</summary>
    public static LoopBlock HalvingRepeat(long startFrame, int lengthFrames, int repeats, int floorFrames)
        => new() { Kind = LoopBlockKind.Loop, StartFrame = startFrame, LengthFrames = lengthFrames, Repeats = repeats, UntilFrame = -1, Halving = true, FloorFrames = floorFrames };

    /// <summary>A halving loop until the original reaches <paramref name="untilFrame"/>.</summary>
    public static LoopBlock HalvingUntil(long startFrame, int lengthFrames, long untilFrame, int floorFrames)
        => new() { Kind = LoopBlockKind.Loop, StartFrame = startFrame, LengthFrames = lengthFrames, UntilFrame = untilFrame, Halving = true, FloorFrames = floorFrames };

    /// <summary>The length of repeat <paramref name="k"/> (1-based).</summary>
    public int RepeatLength(int k)
        => !Halving ? LengthFrames : Math.Max(FloorFrames, k >= 31 ? 0 : LengthFrames >> k);

    /// <summary>The source frame the first repeat starts at (the end of the first pass).</summary>
    public long LoopPointFrame => StartFrame + LengthFrames;

    /// <summary>The source frame the block ends at: after its last repeat, or at <see cref="UntilFrame"/>.</summary>
    public long EndFrame
    {
        get
        {
            if (Repeats <= 0) return UntilFrame;
            long end = LoopPointFrame;
            for (int k = 1; k <= Repeats; k++) end += RepeatLength(k);
            return UntilFrame >= 0 ? Math.Min(end, UntilFrame) : end;
        }
    }
}

/// <summary>
/// #48 E8: a voice's loop schedule — blocks in source-frame order, what happens after the last one, and the seam every loop point
/// is crossfaded over (equal power, 5 ms by default). Immutable, built off the render thread; arm it with
/// <see cref="PcmAudioSession.SetVoiceLoop"/> (the session allocates the capture buffer, <see cref="CaptureFrames"/> long, there).
/// </summary>
public sealed class BeatLoopSchedule
{
    private readonly LoopBlock[] _blocks;
    private readonly long[] _ends;

    /// <summary>Validate and freeze <paramref name="blocks"/>. Throws for a reserved block type (<see cref="LoopBlockKind.Spinback"/>,
    /// <see cref="LoopBlockKind.VinylStop"/>), a region or repeat shorter than two seams, blocks out of order or overlapping (each must
    /// start a seam after the previous one ends), or a block that never ends.</summary>
    public BeatLoopSchedule(ReadOnlySpan<LoopBlock> blocks, LoopEnd end, int sampleRate, double seamMs = 5.0)
    {
        if (blocks.IsEmpty) throw new ArgumentException("A loop schedule needs at least one block.", nameof(blocks));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SeamFrames = Math.Max(1, (int)Math.Round(seamMs * sampleRate / 1000.0));
        End = end;
        _blocks = blocks.ToArray();
        _ends = new long[_blocks.Length];
        int longest = 0;
        long previousEnd = long.MinValue;
        for (int i = 0; i < _blocks.Length; i++)
        {
            var b = _blocks[i];
            if (b.Kind != LoopBlockKind.Loop)
                throw new NotSupportedException($"Loop block type {b.Kind} is reserved: only LOOP blocks render (SPINBACK and VINYL_STOP are not implemented).");
            if (b.StartFrame < 0) throw new ArgumentOutOfRangeException(nameof(blocks), "A block starts at a negative frame.");
            if (b.LengthFrames < 2 * SeamFrames) throw new ArgumentOutOfRangeException(nameof(blocks), "A loop must be at least two seams long.");
            if (b.Halving && (b.FloorFrames < 2 * SeamFrames || b.FloorFrames > b.LengthFrames))
                throw new ArgumentOutOfRangeException(nameof(blocks), "A halving floor must lie between two seams and the loop length.");
            if (b.Repeats < 0 || b.Repeats <= 0 && b.UntilFrame <= b.LoopPointFrame)
                throw new ArgumentOutOfRangeException(nameof(blocks), "A block needs repeats or an end past its loop point.");
            if (previousEnd != long.MinValue && b.StartFrame < previousEnd + SeamFrames)
                throw new ArgumentException("Blocks must be in order and start a seam after the previous block ends.", nameof(blocks));
            _ends[i] = previousEnd = b.EndFrame;
            longest = Math.Max(longest, b.LengthFrames);
        }
        CaptureFrames = longest + SeamFrames;
        SeamIn = new float[SeamFrames];
        SeamOut = new float[SeamFrames];
        for (int j = 0; j < SeamFrames; j++)
        {
            float p = (j + 0.5f) / SeamFrames;
            SeamIn[j] = CrossfadeCurves.In(CrossCurve.EqualPower, p);
            SeamOut[j] = CrossfadeCurves.Out(CrossCurve.EqualPower, p);
        }
    }

    /// <summary>The blocks, in order.</summary>
    public ReadOnlySpan<LoopBlock> Blocks => _blocks;
    /// <summary>What happens after the last block.</summary>
    public LoopEnd End { get; }
    /// <summary>Frames every loop point (and the block end) is crossfaded over.</summary>
    public int SeamFrames { get; }
    /// <summary>Frames of audio the voice must hold to loop the longest block (its region plus one seam of what follows it).</summary>
    public int CaptureFrames { get; }
    internal float[] SeamIn { get; }
    internal float[] SeamOut { get; }
    internal int Count => _blocks.Length;
    internal ref readonly LoopBlock Block(int i) => ref _blocks[i];
    internal long EndOf(int i) => _ends[i];
}

/// <summary>The phase of a <see cref="BeatLoopSource"/>.</summary>
public enum LoopPhase : byte
{
    /// <summary>Playing the original, waiting for the next block's start.</summary>
    Idle,
    /// <summary>The block's first pass (original audio, captured).</summary>
    Capture,
    /// <summary>Repeating the captured region.</summary>
    Repeat,
    /// <summary>The seam after the block's end (back to the original, or to silence).</summary>
    EndSeam,
    /// <summary>No block left: the original passes through.</summary>
    Done,
    /// <summary>The voice stopped (<see cref="LoopEnd.Stop"/>).</summary>
    Stopped,
}

/// <summary>#48 E8: what a voice's loop has done (control-thread reads of render-published values).</summary>
/// <param name="Phase">The loop's phase.</param>
/// <param name="Block">The current block index.</param>
/// <param name="Repeat">The current repeat (0 = the first pass).</param>
/// <param name="Boundaries">Loop points and block ends passed so far.</param>
/// <param name="LastBoundaryFrame">The original's source frame at the newest boundary.</param>
public readonly record struct VoiceLoopReading(LoopPhase Phase, int Block, int Repeat, long Boundaries, long LastBoundaryFrame);

/// <summary>A prepared arm: the schedule, its capture buffer and a source shell for a voice that has no loop yet. Built off the
/// render thread by <see cref="PcmAudioSession.SetVoiceLoop"/>.</summary>
internal sealed class BeatLoopArm(BeatLoopSchedule? schedule, float[]? capture, BeatLoopSource shell)
{
    public BeatLoopSchedule? Schedule { get; } = schedule;
    public float[]? Capture { get; } = capture;
    public BeatLoopSource Shell { get; } = shell;
}

/// <summary>
/// #48 E8: a beat-loop wrapper over a voice's source. It reads the inner source 1:1 (the original runs on underneath, so the
/// position never leaves the original's timeline: <see cref="PositionFrames"/> is the inner's) and rewrites, in place, the frames
/// that fall inside a block's repeats with the captured region. The region is captured as it plays (its first pass is the original)
/// into a buffer allocated when the schedule is armed; every loop point is an equal-power crossfade from what would have followed
/// to the loop's start, and the block end one from the loop back to the original (or to silence for <see cref="LoopEnd.Stop"/>), so
/// no loop point clicks. Allocation-free and lock-free on the render thread.
/// <para>On a voice it sits between the inner source and the voice's <see cref="WsolaAudioSource"/>, so a loop follows the voice's
/// rate. Standalone (<see cref="BeatLoopSource(IAudioSource, int, BeatLoopSchedule)"/>) it is a plain <see cref="IAudioSource"/>.</para>
/// </summary>
public sealed class BeatLoopSource : IAudioSource
{
    private readonly IAudioSource _inner;
    private readonly int _channels;
    private readonly float[] _scratch;    // one frame: the loop side of the end seam
    private BeatLoopSchedule? _schedule;
    private float[]? _capture;
    private BeatLoopSchedule? _pendingSchedule;
    private float[]? _pendingCapture;
    private bool _hasPending;
    private long _pos;                    // the original's source frame of the next frame read from the inner source
    private int _block;
    private LoopPhase _phase = LoopPhase.Done;
    private long _blockStart, _blockEnd;
    private int _length, _captureLimit;
    private long _repStart;
    private int _repLen, _prevLen, _repIdx;
    private int _endOff, _seamDone;
    private bool _endStops;
    private long _boundaries, _lastBoundary = long.MinValue;

    /// <summary>A shell over <paramref name="inner"/> with nothing armed (the session's path: armed on the render thread).</summary>
    internal BeatLoopSource(IAudioSource inner, int channels)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _channels = Math.Max(1, channels);
        _scratch = new float[_channels];
    }

    /// <summary>A standalone loop over <paramref name="inner"/> (allocates the capture buffer here, off the render thread).</summary>
    public BeatLoopSource(IAudioSource inner, int channels, BeatLoopSchedule schedule) : this(inner, channels)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        Bind();
        Adopt(schedule, new float[schedule.CaptureFrames * _channels]);
    }

    /// <inheritdoc/>
    public long PositionFrames => _inner.PositionFrames;
    /// <inheritdoc/>
    public bool Exhausted => _phase == LoopPhase.Stopped || _inner.Exhausted;
    /// <inheritdoc/>
    public GaplessInfo Gapless => _inner.Gapless;
    /// <inheritdoc/>
    public ReplayGainInfo Loudness => _inner.Loudness;
    /// <summary>The loop's current phase.</summary>
    public LoopPhase Phase => _phase;

    /// <summary>What the loop has done (any thread; values published by the render thread).</summary>
    public VoiceLoopReading ReadState() => new(_phase, Volatile.Read(ref _block), Volatile.Read(ref _repIdx),
        Volatile.Read(ref _boundaries), Volatile.Read(ref _lastBoundary));

    /// <summary>RENDER thread: start tracking the inner source at its current read position.</summary>
    internal void Bind() => _pos = _inner.PositionFrames;

    /// <summary>RENDER thread: arm <paramref name="schedule"/> (null ends the current one). A block still sounding ends first, with a
    /// seam back to the original; the new schedule then applies from the original's position on (a block whose start has passed is
    /// skipped: its region can no longer be captured whole).</summary>
    internal void Arm(BeatLoopSchedule? schedule, float[]? capture)
    {
        if (_phase == LoopPhase.Stopped) return;
        if (_phase == LoopPhase.Repeat) BeginEndSeam(_pos, stops: false);
        if (_phase == LoopPhase.EndSeam)
        {
            _endStops = false;
            _pendingSchedule = schedule;
            _pendingCapture = capture;
            _hasPending = true;
            return;
        }
        Adopt(schedule, capture);
    }

    /// <summary>RENDER thread: the voice jumped to <paramref name="sourceFrame"/> (a seek). A block in progress is dropped without a seam
    /// (the seek carries its own blend) and the schedule re-applies from the new position.</summary>
    internal void Discontinuity(long sourceFrame)
    {
        _pos = sourceFrame;
        if (_phase == LoopPhase.Stopped) return;
        if (_hasPending) { Adopt(_pendingSchedule, _pendingCapture); return; }
        if (_schedule is null) { _phase = LoopPhase.Done; return; }
        Volatile.Write(ref _block, 0);
        _phase = LoopPhase.Idle;
    }

    private void Adopt(BeatLoopSchedule? schedule, float[]? capture)
    {
        _schedule = schedule;
        _capture = capture;
        _hasPending = false;
        _pendingSchedule = null;
        _pendingCapture = null;
        Volatile.Write(ref _block, 0);
        Volatile.Write(ref _repIdx, 0);
        _phase = schedule is null || capture is null || capture.Length < schedule.CaptureFrames * _channels ? LoopPhase.Done : LoopPhase.Idle;
        if (_phase == LoopPhase.Done) _schedule = null;
    }

    /// <inheritdoc/>
    public int Read(Span<float> dst, int channels)
    {
        if (_phase == LoopPhase.Stopped) return 0;
        int got = _inner.Read(dst, channels);
        if (got <= 0) return got;
        if (_phase == LoopPhase.Done || _phase == LoopPhase.Idle && _schedule!.Block(_block).StartFrame >= _pos + got)
        {
            _pos += got;   // nothing to loop in these frames: the original passes through untouched
            return got;
        }
        int kept = Process(dst, got);
        _pos += got;
        return kept;
    }

    /// <summary>Rewrite the frames at source positions [_pos, _pos + n) in place; returns the frames to emit (fewer once the voice stops).</summary>
    private int Process(Span<float> buf, int n)
    {
        int ch = _channels;
        for (int i = 0; i < n; i++)
        {
            long p = _pos + i;
            var frame = buf.Slice(i * ch, ch);
            while (true)
            {
                switch (_phase)
                {
                    case LoopPhase.Idle:
                        if (!EnterBlockIfDue(p)) goto next;
                        continue;
                    case LoopPhase.Capture:
                        Capture(p, frame);
                        if (p < _blockStart + _length) goto next;   // the first pass is the original
                        _repIdx = 1;
                        _prevLen = _length;
                        _repLen = _schedule!.Block(_block).RepeatLength(1);
                        _repStart = p;
                        Boundary(p);
                        _phase = LoopPhase.Repeat;
                        continue;
                    case LoopPhase.Repeat:
                    {
                        Capture(p, frame);   // what follows the region in the original: the first seam and every later one blend from it
                        if (p >= _blockEnd) { BeginEndSeam(p, stops: _schedule!.End == LoopEnd.Stop && _block == _schedule.Count - 1); continue; }
                        int off = (int)(p - _repStart);
                        if (off >= _repLen)
                        {
                            _prevLen = _repLen;
                            Volatile.Write(ref _repIdx, _repIdx + 1);
                            _repLen = _schedule!.Block(_block).RepeatLength(_repIdx);
                            _repStart = p;
                            off = 0;
                            Boundary(p);
                        }
                        LoopFrame(off, frame);
                        goto next;
                    }
                    case LoopPhase.EndSeam:
                    {
                        var s = _schedule!;
                        int j = _seamDone;
                        float wOut = s.SeamOut[j], wIn = _endStops ? 0f : s.SeamIn[j];
                        Span<float> loop = _scratch;
                        LoopFrame(_endOff + j, loop);
                        for (int c = 0; c < ch; c++) frame[c] = loop[c] * wOut + frame[c] * wIn;
                        if (++_seamDone < s.SeamFrames) goto next;
                        if (_endStops)
                        {
                            _phase = LoopPhase.Stopped;
                            return i + 1;
                        }
                        if (_hasPending) Adopt(_pendingSchedule, _pendingCapture);
                        else
                        {
                            Volatile.Write(ref _block, _block + 1);
                            _phase = _block < s.Count ? LoopPhase.Idle : LoopPhase.Done;
                        }
                        goto next;
                    }
                    case LoopPhase.Stopped:
                        return i;
                    default:   // Done
                        goto next;
                }
            }
        next:;
        }
        return n;
    }

    /// <summary>Idle: skip blocks whose start has passed (they can no longer be captured whole); enter the next one on its first frame.</summary>
    private bool EnterBlockIfDue(long p)
    {
        var s = _schedule!;
        int block = _block;
        while (block < s.Count && s.Block(block).StartFrame < p) block++;
        Volatile.Write(ref _block, block);
        if (block >= s.Count) { _phase = LoopPhase.Done; return false; }
        ref readonly var b = ref s.Block(block);
        if (b.StartFrame != p) return false;
        _blockStart = b.StartFrame;
        _length = b.LengthFrames;
        _blockEnd = s.EndOf(block);
        _captureLimit = Math.Min(_length + s.SeamFrames, s.CaptureFrames);
        Volatile.Write(ref _repIdx, 0);
        _phase = LoopPhase.Capture;
        return true;
    }

    private void Capture(long p, ReadOnlySpan<float> frame)
    {
        long idx = p - _blockStart;
        if (idx < 0 || idx >= _captureLimit) return;
        frame.CopyTo(_capture.AsSpan((int)idx * _channels, _channels));
    }

    /// <summary>The loop's frame at offset <paramref name="off"/> into the current repeat: inside its first seam, the equal-power blend
    /// of what followed the previous pass and the region's start; past the repeat's end (in the block's end seam), the region's
    /// natural continuation.</summary>
    private void LoopFrame(int off, Span<float> dst)
    {
        var s = _schedule!;
        var cap = _capture!;
        int ch = _channels;
        if (off < s.SeamFrames)
        {
            float wIn = s.SeamIn[off], wOut = s.SeamOut[off];
            int a = (_prevLen + off) * ch, b = off * ch;
            for (int c = 0; c < ch; c++) dst[c] = cap[a + c] * wOut + cap[b + c] * wIn;
            return;
        }
        int at = Math.Min(off, _captureLimit - 1) * ch;
        for (int c = 0; c < ch; c++) dst[c] = cap[at + c];
    }

    private void BeginEndSeam(long p, bool stops)
    {
        _endOff = (int)(p - _repStart);
        _seamDone = 0;
        _endStops = stops;
        _phase = LoopPhase.EndSeam;
        Boundary(p);
    }

    private void Boundary(long p)
    {
        Volatile.Write(ref _lastBoundary, p);
        Volatile.Write(ref _boundaries, _boundaries + 1);
    }
}

public sealed partial class PcmAudioSession
{
    /// <summary>#48 E8: arm <paramref name="schedule"/> on voice <paramref name="id"/>'s loop (null ends a loop that is sounding, with a
    /// seam back to the original). The capture buffer is allocated here, off the render thread; the loop is inserted at the voice's
    /// current source position at the next block boundary, so arm it before the first block's start (a block whose start has
    /// passed by then is skipped). False when the voice is unknown or the command could not be admitted. Control thread.</summary>
    public bool SetVoiceLoop(long id, BeatLoopSchedule? schedule)
    {
        if (!_rateSources.TryGetValue(id, out var source)) return false;
        int ch = _format.Channels;
        var arm = new BeatLoopArm(schedule, schedule is null ? null : new float[schedule.CaptureFrames * ch], new BeatLoopSource(source.Inner, ch));
        return TryEnqueueMixerCmd(new MixerCmd { Kind = CmdSetLoop, Id = id, LoopArm = arm }, out _);
    }

    /// <summary>#48 E8: what voice <paramref name="id"/>'s loop has done. False when the voice is unknown or never had a loop. Control thread.</summary>
    public bool TryReadVoiceLoop(long id, out VoiceLoopReading reading)
    {
        if (_rateSources.TryGetValue(id, out var source) && source.Loop is { } loop) { reading = loop.ReadState(); return true; }
        reading = default;
        return false;
    }
}
