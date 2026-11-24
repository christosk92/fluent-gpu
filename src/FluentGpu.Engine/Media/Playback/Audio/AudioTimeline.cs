using System;
using System.Diagnostics;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>What ONE voice of a <see cref="PcmAudioSession"/> is playing, anchored to the device clock: the voice named by
/// <see cref="VoiceId"/> (the session's active voice, the one the host last made active with <c>SetActiveVoice</c>). Every
/// field but <see cref="MixFrame"/> is that voice's, never another voice's. A straight line in time: extrapolate it with the
/// <c>*At</c> methods, never store a position. While <see cref="Epoch"/> holds, every anchor lies on the same line (only
/// re-sampled); a new epoch is a discontinuity (a seek, an active-voice change, a rate change, a start or stop, a device
/// rebuild, or the playhead leaving the line by more than 2 ms).</summary>
/// <param name="Qpc100ns">The instant the anchor is true for: a QPC reading in 100-ns units (<see cref="NowQpc100ns"/>), the same
/// domain as <see cref="IAudioClockSource.TryGetPlayed"/>'s timestamp (a session over an <see cref="IAudioTimebase"/> clock stamps
/// in that clock's domain instead: read now from <see cref="PcmAudioSession.TimelineNowQpc100ns"/>).</param>
/// <param name="ContentFrame">The session position the engine publishes as <c>Position</c>, AUDIBLE (stream and DSP latency
/// subtracted): continuous across the session's voices, so an identity that started mid-session subtracts its start.</param>
/// <param name="SourceFrame">The audible frame INSIDE the active voice's own source (its start position included, so a voice
/// started past its head reads that offset at its first frame). Equals <see cref="PcmAudioSession.ContentPositionFrames"/>.</param>
/// <param name="MixFrame">The audible frame in the mixer domain (<see cref="PcmAudioSession.SampleClock"/>): where scheduled
/// envelopes and transitions live. Advances at <see cref="MixRate"/> whatever the voice's rate is, and is never re-based by a
/// seek.</param>
/// <param name="MixRate">Frames per second of all three frame fields.</param>
/// <param name="Rate">Source frames per output frame of the active voice (the session's one rate, <see cref="PcmAudioSession.PlaybackRate"/>).</param>
/// <param name="Advancing">False while paused, held, starved or buffering: the frames hold.</param>
/// <param name="VoiceId">The active voice (<see cref="PcmAudioSession.ActiveVoiceIdValue"/>).</param>
/// <param name="Epoch">Bumps on every discontinuity; never 0 once published.</param>
public readonly record struct AudioTimelineAnchor(
    long Qpc100ns, double ContentFrame, double SourceFrame, double MixFrame, int MixRate, double Rate, bool Advancing, long VoiceId, long Epoch)
{
    private static readonly double s_qpcTo100ns = 1e7 / Stopwatch.Frequency;

    /// <summary>Now, in the anchor's clock domain (QPC in 100-ns units). Allocation-free; any thread.</summary>
    public static long NowQpc100ns() => (long)(Stopwatch.GetTimestamp() * s_qpcTo100ns);

    /// <summary>The session position (<see cref="ContentFrame"/>) at <paramref name="qpc100ns"/>; holds when not advancing.</summary>
    public double ContentFrameAt(long qpc100ns) => Advancing ? ContentFrame + Elapsed(qpc100ns) * Rate : ContentFrame;

    /// <summary>The active voice's source frame at <paramref name="qpc100ns"/>; holds when not advancing.</summary>
    public double SourceFrameAt(long qpc100ns) => Advancing ? SourceFrame + Elapsed(qpc100ns) * Rate : SourceFrame;

    /// <summary>The audible mixer frame at <paramref name="qpc100ns"/> (advances at <see cref="MixRate"/>, never at the voice's rate).</summary>
    public double MixFrameAt(long qpc100ns) => Advancing ? MixFrame + Elapsed(qpc100ns) : MixFrame;

    /// <summary>The instant a mixer frame (a scheduled transition edge) is heard, seen from this anchor: valid while the epoch
    /// holds and the session advances (a pause in between moves it; re-derive it from each new anchor).</summary>
    public long QpcOfMixFrame(long mixFrame) => Qpc100ns + (long)((mixFrame - MixFrame) * 1e7 / Math.Max(1, MixRate));

    double Elapsed(long qpc100ns) => (qpc100ns - Qpc100ns) / 1e7 * MixRate;
}

/// <summary>Counters of the timeline publisher (diagnostics): anchors published, epochs started, and how many of those epochs
/// started only because the playhead left the extrapolated line by more than the tolerance (a jump no explicit trigger named:
/// an in-ring jump, a clock step).</summary>
public readonly record struct AudioTimelineStats(long Publishes, long Epochs, long DriftEpochs, double LastDriftMs);

/// <summary>The anchor's hand-off: a ring of four slots, each a seqlock (odd version while written, even when whole), and a
/// sequence naming the newest whole slot. One writer at a time (the publisher holds the session's timeline gate); readers never
/// wait, never allocate and never take a lock. A reader fails only when the writer laps it, four publishes (about 60 ms at the
/// control tick's rate) inside one read: a single-slot seqlock failed a reader whenever the writer was merely preempted mid-write,
/// and a generator chasing the tap would read that as "stopped". The array is allocated once per session.</summary>
internal sealed class AudioTimelineMailbox
{
    private struct Slot
    {
        public long Version, Qpc, Content, Source, Mix, Rate, Voice, Epoch;
        public int MixRate, Advancing;
    }

    private readonly Slot[] _slots = new Slot[4];
    private long _sequence;   // the newest whole slot's sequence; 0 = nothing published yet

    internal void Publish(in AudioTimelineAnchor a)
    {
        long sequence = _sequence + 1;
        ref Slot slot = ref _slots[sequence & 3];
        Interlocked.Exchange(ref slot.Version, sequence * 2 + 1);   // full fence: the payload writes stay after the odd mark
        slot.Qpc = a.Qpc100ns;
        slot.Content = BitConverter.DoubleToInt64Bits(a.ContentFrame);
        slot.Source = BitConverter.DoubleToInt64Bits(a.SourceFrame);
        slot.Mix = BitConverter.DoubleToInt64Bits(a.MixFrame);
        slot.Rate = BitConverter.DoubleToInt64Bits(a.Rate);
        slot.Voice = a.VoiceId;
        slot.Epoch = a.Epoch;
        slot.MixRate = a.MixRate;
        slot.Advancing = a.Advancing ? 1 : 0;
        Volatile.Write(ref slot.Version, sequence * 2 + 2);          // release: the payload is visible before the even mark
        Volatile.Write(ref _sequence, sequence);
    }

    internal bool TryRead(out AudioTimelineAnchor anchor)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            long sequence = Volatile.Read(ref _sequence);
            if (sequence == 0) break;
            ref Slot slot = ref _slots[sequence & 3];
            long version = Volatile.Read(ref slot.Version);
            if (version != sequence * 2 + 2) continue;               // lapped: a newer publish is rewriting this slot
            long qpc = slot.Qpc, content = slot.Content, source = slot.Source, mix = slot.Mix, rate = slot.Rate, voice = slot.Voice, epoch = slot.Epoch;
            int mixRate = slot.MixRate, advancing = slot.Advancing;
            // Full fence keeps the payload reads before the validation read, including on ARM64.
            Thread.MemoryBarrier();
            if (Volatile.Read(ref slot.Version) != version) continue;
            anchor = new AudioTimelineAnchor(qpc, BitConverter.Int64BitsToDouble(content), BitConverter.Int64BitsToDouble(source),
                BitConverter.Int64BitsToDouble(mix), mixRate, BitConverter.Int64BitsToDouble(rate), advancing != 0, voice, epoch);
            return true;
        }
        anchor = default;
        return false;
    }
}

public sealed partial class PcmAudioSession
{
    // ── the playback timeline tap (#154 G2a): what is audible, anchored to the device clock ─────────────────────────────
    private readonly AudioTimelineMailbox _timeline = new();
    private readonly object _timelineGate = new();   // publishers only (the control tick and the pause path); readers never take it
    private long _timelineEpoch, _timelineRenderEpoch = -1, _timelineSeekRevision = -1;
    private long _timelinePublishes, _timelineDriftEpochs;
    private double _timelineLastDriftMs;

    /// <summary>How far the playhead may leave the extrapolated line before the publisher calls it a discontinuity.</summary>
    private const double TimelineDriftToleranceSeconds = 0.002;

    /// <summary>How far ahead the publisher looks to see whether the source frame moves (the hold test).</summary>
    private const double TimelineProbeSeconds = 0.002;

    /// <summary>The playback timeline tap: the active voice's audible position anchored to a QPC instant. Any thread, wait-free,
    /// allocation-free. A session publishes a held anchor when its signals are connected, then on every control tick while its
    /// device runs, on pause, and at the end of every replacement or seek transaction. False only before the first.</summary>
    public bool TryReadTimeline(out AudioTimelineAnchor anchor) => _timeline.TryRead(out anchor);

    /// <summary>Now, in the domain this session's anchors are stamped in: the machine's QPC (<see cref="AudioTimelineAnchor.NowQpc100ns"/>)
    /// unless the endpoint's clock is an <see cref="IAudioTimebase"/> (a synthetic or frame-paced clock). Read "now" here to
    /// extrapolate this session's anchors. Any thread.</summary>
    public long TimelineNowQpc100ns => NowTicks100ns();

    /// <summary>The timeline publisher's counters (diagnostics).</summary>
    public AudioTimelineStats TimelineStats => new(Interlocked.Read(ref _timelinePublishes), Interlocked.Read(ref _timelineEpoch),
        Interlocked.Read(ref _timelineDriftEpochs), Volatile.Read(ref _timelineLastDriftMs));

    /// <summary>Publish an anchor from the values <see cref="PublishPosition"/> just computed. <paramref name="played"/> and
    /// <paramref name="playedQpc"/> are the content clock's latest poll; the mixer frame is derived from it (the mixer's
    /// <c>ConsumeSeq</c> minus what is still queued between the mixer and the ear), never from the position domain a seek
    /// re-bases. Control thread (or the pause path); never the RT thread. Allocation-free.</summary>
    private void PublishTimeline(long now, double clockFrame, double contentFrame, double sourceFrame, long played, long playedQpc, bool playedValid)
    {
        int mixRate = _format.SampleRate;
        double rate = PlaybackRate;
        // Advancing only while the audible frame really moves: playing, not starved, the projection not held (the warm-up gate, a
        // refill at the submitted limit) and the active voice's source frame moving over the next 2 ms of the clock. The last test
        // is the seek: until the new position's first frame reaches the ear (the device still plays out the hold), the source
        // mapping holds at the target. Measured against a loopback capture, a seek used to publish ~120 ms of "advancing" anchors
        // whose frame stood still, and every one of them started a drift epoch.
        double probeFrames = TimelineProbeSeconds * mixRate;
        bool advancing = _state == PlaybackState.Playing && _starvationPhase == 0 && !_position.IsHeldAt(now)
                         && ContentFrameAt(clockFrame + probeFrames) - sourceFrame > probeFrames * rate * 0.5;
        long pendingContent = _pendingSilence ? 0 : Volatile.Read(ref _pendingFrames);
        long domainOffset = ConsumeSeqFrames - ContentSubmittedFrames() - pendingContent;
        double mixFrame = played + domainOffset - _clock.StreamLatencyFrames - _position.ExtraLatencySamples;
        if (advancing && playedValid && playedQpc > 0 && now > playedQpc) mixFrame += (now - playedQpc) / 1e7 * mixRate;
        long voice = ActiveVoiceIdValue;
        long renderEpoch = RenderEpoch, seekRevision = Interlocked.Read(ref _seekRevision);
        lock (_timelineGate)
        {
            bool had = _timeline.TryRead(out AudioTimelineAnchor prev);
            double driftMs = had ? Math.Abs(prev.SourceFrameAt(now) - sourceFrame) * 1000.0 / Math.Max(1, mixRate) : 0;
            bool explicitBreak = !had || prev.VoiceId != voice || prev.Rate != rate || prev.Advancing != advancing
                                 || prev.MixRate != mixRate || renderEpoch != _timelineRenderEpoch || seekRevision != _timelineSeekRevision;
            bool drifted = !explicitBreak && driftMs > TimelineDriftToleranceSeconds * 1000.0;
            long epoch = _timelineEpoch;
            if (explicitBreak || drifted)
            {
                epoch = Interlocked.Increment(ref _timelineEpoch);
                if (drifted) { Interlocked.Increment(ref _timelineDriftEpochs); Volatile.Write(ref _timelineLastDriftMs, driftMs); }
            }
            _timelineRenderEpoch = renderEpoch;
            _timelineSeekRevision = seekRevision;
            _timeline.Publish(new AudioTimelineAnchor(now, contentFrame, sourceFrame, mixFrame, mixRate, rate, advancing, voice, epoch));
            Interlocked.Increment(ref _timelinePublishes);
        }
    }
}
