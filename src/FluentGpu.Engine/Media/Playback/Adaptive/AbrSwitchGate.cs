using System;

namespace FluentGpu.Media.Adaptive;

/// <summary>
/// The two pacing rules a representation switch needs on top of <see cref="AdaptiveBitrateController"/>'s verdicts, as a
/// pure type so they are unit-testable without a session or a clock: a MINIMUM INTERVAL between ABR-initiated switches
/// (Shaka's <c>switchInterval</c>, 8 s) and the rule that a FORCED PROBE is judged only on evidence from its own rung.
/// <para><b>Interval.</b> Every switch costs two round trips and, under a different bitrate, a visible quality change; a
/// controller deciding at 1 Hz would otherwise be free to change rung every second. A switch inside the interval is
/// refused unless it is an emergency (a DECREASE with the forward buffer under <see cref="EmergencyBufferMs"/>) or the
/// controller's own reason is a cap or a pin: a cap is not a throughput verdict and the surface it describes is already
/// the wrong size.</para>
/// <para><b>Probe evidence.</b> A forced probe steps above what the estimate can justify, so the very next decision
/// would revert it (the rung is over the 0.95 sustain budget) before one byte of the probe rung had been measured — a
/// failed-probe tally for a probe that was never tested, and two splices for nothing. While a probe is unjudged the
/// caller holds its decisions until the probe rung has landed AND at least one throughput sample has been accepted since
/// (or the buffer is in emergency territory, where the verdict cannot wait).</para>
/// </summary>
public sealed class AbrSwitchGate
{
    /// <summary>Default minimum gap between ABR-initiated switches (ms).</summary>
    public const long DefaultMinSwitchIntervalMs = 8_000;
    /// <summary>Default forward buffer under which a decrease may break the interval (ms).</summary>
    public const long DefaultEmergencyBufferMs = 10_000;

    private long _lastSwitchMs;
    private bool _hasSwitched;
    private bool _probeUnjudged;
    private bool _probeLanded;
    private int _probeBaselineSamples;

    /// <summary>Minimum gap between ABR-initiated switches, measured request to request (ms).</summary>
    public long MinSwitchIntervalMs { get; set; } = DefaultMinSwitchIntervalMs;
    /// <summary>A decrease with less forward buffer than this (ms) may break the interval, and a probe verdict no longer
    /// waits for evidence.</summary>
    public long EmergencyBufferMs { get; set; } = DefaultEmergencyBufferMs;

    /// <summary>True while a forced probe has been requested and not yet judged.</summary>
    public bool ProbeUnjudged => _probeUnjudged;

    /// <summary>True when no switch was requested within <see cref="MinSwitchIntervalMs"/> of <paramref name="nowMs"/>.</summary>
    public bool IntervalElapsed(long nowMs) => !_hasSwitched || nowMs - _lastSwitchMs >= MinSwitchIntervalMs;

    /// <summary>Whether the controller's pick may become a switch request now. <paramref name="isDecrease"/> is the pick's
    /// bitrate being below the rung in use; <paramref name="reason"/> is the controller's own reason for it.</summary>
    public bool Allows(long nowMs, bool isDecrease, long forwardBufferedMs, AbrDecisionReason reason)
    {
        if (IntervalElapsed(nowMs)) return true;
        if (reason is AbrDecisionReason.CapDownswitch or AbrDecisionReason.Capped or AbrDecisionReason.Pinned) return true;
        return isDecrease && forwardBufferedMs < EmergencyBufferMs;
    }

    /// <summary>A switch was requested at <paramref name="nowMs"/>: the interval restarts.</summary>
    public void NoteSwitch(long nowMs)
    {
        _lastSwitchMs = nowMs;
        _hasSwitched = true;
    }

    /// <summary>A forced probe was requested: its verdict waits for evidence from its own rung.</summary>
    public void BeginProbe()
    {
        _probeUnjudged = true;
        _probeLanded = false;
        _probeBaselineSamples = 0;
    }

    /// <summary>The probe rung is now the one being downloaded; samples accepted AFTER this count as its evidence.</summary>
    public void ProbeLanded(int acceptedSamples)
    {
        if (!_probeUnjudged || _probeLanded) return;
        _probeLanded = true;
        _probeBaselineSamples = acceptedSamples;
    }

    /// <summary>The probe is over (judged, abandoned, or its switch never landed).</summary>
    public void EndProbe()
    {
        _probeUnjudged = false;
        _probeLanded = false;
    }

    /// <summary>True when the caller must skip this decision: a probe is unjudged, its rung has not produced a sample of
    /// its own yet, and the buffer is not in emergency territory. Once evidence exists the probe is judged (and this
    /// returns false from then on).</summary>
    public bool HoldForProbeEvidence(int acceptedSamples, long forwardBufferedMs)
    {
        if (!_probeUnjudged) return false;
        if (forwardBufferedMs < EmergencyBufferMs) return false;
        if (_probeLanded && acceptedSamples > _probeBaselineSamples)
        {
            _probeUnjudged = false;
            return false;
        }
        return true;
    }

    /// <summary>Forget every switch and probe (a new source).</summary>
    public void Reset()
    {
        _hasSwitched = false;
        _lastSwitchMs = 0;
        EndProbe();
    }
}
