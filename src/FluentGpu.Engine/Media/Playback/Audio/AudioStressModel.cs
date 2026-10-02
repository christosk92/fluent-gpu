using System;
using System.Collections.Generic;
using System.Globalization;

namespace FluentGpu.Media;

// ── AudioStressModel — the pure, deterministic load-test model of the decode-ahead ring (playback-smoothness plan §4.18, V-PE34) ──
//
// WHAT IT MODELS. Three queues and one policy, stepped one RT block (10 ms) at a time with no threads and no wall clock:
//
//     producer ──(Rate × real time, scripted absences)──▶ RING (ahead target 2 s, low-water wake at half) ──(RT)──▶ DEVICE (100 ms FIFO) ──▶ speakers
//
// Per step: (1) the producer wakes below low water and runs at the schedule's rate until the ring is back at target;
// (2) the RT submits up to MaxBlocksPerWake blocks while the device has room — each submission is decided by the
// IStarvationPolicy (content, silence, or withhold); (3) the device plays one block. What is audible is what the device
// plays: content, or silence the RT submitted, or a hole where the device ran dry. A gap is a maximal run of non-content.
//
// WHAT IT DOES NOT MODEL. OS scheduling, MMCSS, GC pauses inside a thread, WSOLA, crossfades. It decides POLICY questions
// (how long a starve lasts, how often a policy resumes, whether the device is stopped) against a scripted producer; the
// real session is driven by StarvationRecoveryTests, and the real machine by the app's `--stress-audio` runner.
//
// HONEST NUMBERS. The device queue is real, and F2's silence is SHALLOW: RenderSilence tops the device FIFO up with silence only
// while its padding is below PcmAudioSession.SilencePaddingFloorBlocks render blocks (the audio the device already holds plays out
// first; nothing is queued behind it until it is nearly gone), so audio that returns after a long absence reaches the speakers about
// one block after the resume — within ~10 ms of the legacy "withhold, let the device drain, Stop, restart" path — and in exchange F2
// never stops the device and never stutters. (Full-depth silence, the shipped design before WP-1b, queued a whole device buffer of
// it ahead of the returning audio: ~90 ms more gap; the tests still pin that comparison through a policy built with an infinite floor.)
// One consequence the tests also pin: right after a resume the RT refills the (now shallow) device FIFO from the ring in bursts, so the
// ring is ~one device buffer shallower than under full-depth silence and the NEXT starve of a slow producer comes sooner.
//
// Pure: `System` only, allocation is irrelevant (test and probe code), every number is a function of the scenario + policy.

/// <summary>The time-domain constants of the model: the SAME numbers the engine's <c>RingSizing</c> carries (D8: ahead 2 s,
/// ring 4 s, keep-behind 1 s, 10 ms blocks, ≤ 3 blocks per RT wake) plus the device buffer (WASAPI shared, 100 ms). A record
/// CLASS, not a struct: <c>new StressConfig()</c> must carry the defaults (a record struct's <c>new()</c> is all zeros).</summary>
public sealed record StressConfig(
    double BlockMs = 10.0,
    double DeviceBufferMs = 100.0,
    double TargetAheadMs = 2000.0,
    double RingMs = 4000.0,
    double KeepBehindMs = 1000.0,
    int MaxBlocksPerWake = 3)
{
    /// <summary>The shipping numbers.</summary>
    public static StressConfig Default { get; } = new();

    /// <summary>The model's numbers from the engine's own sizing record, so a change to D8's constants moves the model with it.</summary>
    public static StressConfig From(RingSizing sizing, double deviceBufferMs = 100.0, int maxBlocksPerWake = 3)
        => new(sizing.BlockMs, deviceBufferMs, sizing.AheadMs, sizing.RingMs, sizing.KeepBehindMs, maxBlocksPerWake);

    /// <summary>The ceiling <c>GrowAhead</c> may raise the ring's own target to: capacity minus the protected keep-behind span.
    /// Nominal (the real ring rounds its capacity up to a power of two); never below the configured ahead.</summary>
    public double GrowAheadCapMs => Math.Max(TargetAheadMs, RingMs - KeepBehindMs);

    /// <summary>The producer's low-water wake mark for a ring target (half of it).</summary>
    public double LowWaterMs(double targetMs) => targetMs * 0.5;
}

/// <summary>A producer speed override: <paramref name="Rate"/> × real time over [<paramref name="StartMs"/>, <paramref name="EndMs"/>).
/// A zero rate is an ABSENCE (the producer thread descheduled).</summary>
public readonly record struct RateWindow(double StartMs, double EndMs, double Rate)
{
    public bool Contains(double tMs) => tMs >= StartMs && tMs < EndMs;
}

/// <summary>A stop-the-world pause: BOTH the producer and the RT are frozen (a GC suspension, a laptop stall), while the device
/// keeps playing from its buffer.</summary>
public readonly record struct SuspensionWindow(double StartMs, double DurationMs);

/// <summary>What the producer does over time: a base <see cref="Rate"/> (multiples of real time; a healthy decoder is 4× or
/// better) and <see cref="Windows"/> that override it — later windows win; a window with rate 0 is an absence
/// (<see cref="Absences"/>). Immutable: every <c>With…</c> returns a new schedule.</summary>
public sealed class ProducerSchedule
{
    private readonly RateWindow[] _windows;

    private ProducerSchedule(double rate, RateWindow[] windows) { Rate = rate; _windows = windows; }

    /// <summary>The producer's speed outside every window, in multiples of real time.</summary>
    public double Rate { get; }

    /// <summary>Every override, in the order they were added.</summary>
    public IReadOnlyList<RateWindow> Windows => _windows;

    /// <summary>A producer that never changes speed.</summary>
    public static ProducerSchedule Steady(double rate) => new(Math.Max(0.0, rate), Array.Empty<RateWindow>());

    /// <summary>This schedule plus one override.</summary>
    public ProducerSchedule With(double startMs, double durationMs, double rate)
    {
        var next = new RateWindow[_windows.Length + 1];
        Array.Copy(_windows, next, _windows.Length);
        next[^1] = new RateWindow(startMs, startMs + Math.Max(0.0, durationMs), Math.Max(0.0, rate));
        return new ProducerSchedule(Rate, next);
    }

    /// <summary>This schedule plus one absence (rate 0).</summary>
    public ProducerSchedule WithAbsence(double startMs, double durationMs) => With(startMs, durationMs, 0.0);

    /// <summary>The producer's speed at <paramref name="tMs"/>: the LAST window containing it, else <see cref="Rate"/>.</summary>
    public double RateAt(double tMs)
    {
        double rate = Rate;
        for (int i = 0; i < _windows.Length; i++)
            if (_windows[i].Contains(tMs)) rate = _windows[i].Rate;
        return rate;
    }

    /// <summary>True when the producer produces nothing at <paramref name="tMs"/>.</summary>
    public bool IsAbsent(double tMs) => RateAt(tMs) <= 0.0;

    /// <summary>The zero-rate windows.</summary>
    public IEnumerable<RateWindow> Absences
    {
        get
        {
            for (int i = 0; i < _windows.Length; i++)
                if (_windows[i].Rate <= 0.0) yield return _windows[i];
        }
    }
}

/// <summary>One run of the model: how long, what the producer does, which constants, and any stop-the-world pauses.
/// The ring and the device start FULL (steady state) — the scenario describes what goes wrong from t = 0.</summary>
public sealed record StressScenario(string Name, double DurationMs, ProducerSchedule Producer, StressConfig Config,
                                    IReadOnlyList<SuspensionWindow>? Suspensions = null)
{
    /// <summary>True when both threads are frozen at <paramref name="tMs"/>.</summary>
    public bool IsSuspended(double tMs)
    {
        if (Suspensions is null) return false;
        for (int i = 0; i < Suspensions.Count; i++)
        {
            SuspensionWindow s = Suspensions[i];
            if (tMs >= s.StartMs && tMs < s.StartMs + s.DurationMs) return true;
        }
        return false;
    }
}

/// <summary>What the policy sees at one RT submit slot.</summary>
/// <param name="NowMs">Model time at the start of the step.</param>
/// <param name="RingMs">Content buffered in the decode-ahead ring.</param>
/// <param name="DeviceMs">Queued in the device buffer (content or silence).</param>
/// <param name="BlockMs">The size of the block about to be submitted (≤ the RT block; smaller when the device has little room).</param>
/// <param name="AheadMs">The CONFIGURED ahead target — what <c>AudioFeedThread.TargetAheadFrames</c> reports (it is not grown by <c>GrowAhead</c>).</param>
public readonly record struct RtView(double NowMs, double RingMs, double DeviceMs, double BlockMs, double AheadMs);

/// <summary>What the RT does with one submit slot.</summary>
public enum BlockAction : byte
{
    /// <summary>Move up to one block of ring content to the device.</summary>
    Content,
    /// <summary>Submit one block of silence (F2: the device never runs dry — it is topped up only while its padding is below the floor).</summary>
    Silence,
    /// <summary>Submit nothing this wake (the legacy phase 1/2: the device runs on what it has, then is stopped; or F2 with the device
    /// padding already at the shallow floor — it holds enough to play on, so no silence is queued behind it yet).</summary>
    Withhold,
}

/// <summary>Edges the policy reports with a decision.</summary>
[Flags]
public enum StarvationEvents : byte
{
    None = 0,
    /// <summary>The ring just ran empty: ONE incident (the engine's one-xrun-per-incident latch). The model raises it at the edge; the
    /// engine's xrun record and <c>GrowAhead</c> follow once silence is first submitted (shallow F2: when the device padding falls to the
    /// floor — at most one device buffer later), and a starve the device buffer fully absorbs records neither.</summary>
    Incident = 1,
    /// <summary>The policy left the starved state: content flows again.</summary>
    Resume = 2,
    /// <summary>The policy stopped (and will restart) the device — the stop/start cycle F2 exists to remove.</summary>
    DeviceStop = 4,
    /// <summary>The policy asked the ring to grow its ahead target (once per incident).</summary>
    GrowAhead = 8,
}

/// <summary>One decision: the action for this slot and the edges that came with it.</summary>
public readonly record struct StarvationStep(BlockAction Action, StarvationEvents Events = StarvationEvents.None);

/// <summary>The pure contract a starvation policy implements. Stateful (its own phase); <see cref="Reset"/> returns it to the
/// start of a run. <see cref="AudioStressModel.Run"/> resets it, then calls <see cref="Decide"/> once per submit slot.</summary>
public interface IStarvationPolicy
{
    /// <summary>"legacy" or "cushion" — carried into the verdict.</summary>
    string Name { get; }

    /// <summary>The ring level the policy needs before it resumes, in force RIGHT NOW (the cushion changes it per incident).</summary>
    double ResumeThresholdMs { get; }

    /// <summary>Back to the start of a run.</summary>
    void Reset();

    /// <summary>One submit slot.</summary>
    StarvationStep Decide(in RtView view);
}

/// <summary>The pre-F2 two-phase starvation machine (<c>RecoverStarvation</c>): an empty ring withholds writes (phase 1, "drain");
/// when the device has drained it is STOPPED and reset (phase 2); it resumes the moment the ring holds
/// <see cref="ResumeThresholdMs"/> — 20 ms, the WSOLA hop the old <c>PcmReady</c> clamp reduced the readiness to (audit H-2/Q-2) —
/// after a restart delay (the 15 ms clock-thread tick that gated Buffering → Playing). No doubling, no memory of the last incident.</summary>
public sealed class LegacyStarvationPolicy : IStarvationPolicy
{
    public const double DefaultResumeMs = 20.0;
    public const double DefaultRestartMs = 15.0;

    private enum Phase : byte { Running, Draining, Stopped }

    private readonly double _resumeMs;
    private readonly double _restartMs;
    private Phase _phase;
    private double _readySince = -1.0;

    public LegacyStarvationPolicy(double resumeMs = DefaultResumeMs, double restartMs = DefaultRestartMs)
    {
        _resumeMs = resumeMs;
        _restartMs = restartMs;
    }

    public string Name => "legacy";
    public double ResumeThresholdMs => _resumeMs;

    public void Reset() { _phase = Phase.Running; _readySince = -1.0; }

    public StarvationStep Decide(in RtView view)
    {
        switch (_phase)
        {
            case Phase.Running:
                if (view.RingMs > AudioStressModel.Epsilon) return new StarvationStep(BlockAction.Content);
                _phase = Phase.Draining;
                return new StarvationStep(BlockAction.Withhold, StarvationEvents.Incident);

            case Phase.Draining:
                // "Recover before the queued cushion drains when possible": a ring that refills in time resumes with no audible gap.
                if (view.RingMs >= _resumeMs)
                {
                    _phase = Phase.Running;
                    return new StarvationStep(BlockAction.Content, StarvationEvents.Resume);
                }
                if (view.DeviceMs <= AudioStressModel.Epsilon)
                {
                    _phase = Phase.Stopped;
                    _readySince = -1.0;
                    return new StarvationStep(BlockAction.Withhold, StarvationEvents.DeviceStop);
                }
                return new StarvationStep(BlockAction.Withhold);

            default: // Stopped: wait for the ring, then for the restart
                if (view.RingMs < _resumeMs) { _readySince = -1.0; return new StarvationStep(BlockAction.Withhold); }
                if (_readySince < 0.0) _readySince = view.NowMs;
                if (view.NowMs - _readySince < _restartMs) return new StarvationStep(BlockAction.Withhold);
                _phase = Phase.Running;
                _readySince = -1.0;
                return new StarvationStep(BlockAction.Content, StarvationEvents.Resume);
        }
    }
}

/// <summary>F2 (plan §2.3, §4.3): an empty ring submits SILENCE (never Stop/Reset) — SHALLOW silence: only while the device's padding is
/// below <see cref="PcmAudioSession.SilencePaddingFloorBlocks"/> render blocks (the engine's <c>RenderSilence</c> floor), otherwise
/// nothing is submitted and the device plays on what it holds; one incident per starve; the policy resumes only once the ring holds
/// <see cref="ResumeThresholdMs"/> — 100 ms, doubled at every resume up to the configured ahead, halved after 30 s without an incident —
/// and asks the ring to grow its ahead target once per incident.</summary>
public sealed class CushionStarvationPolicy : IStarvationPolicy
{
    public const double DefaultInitialResumeMs = 100.0;
    public const double DecayAfterMs = 30_000.0;

    private readonly double _initialMs;
    private readonly double _floorBlocks;
    private double _resumeMs;
    private double _lastIncidentMs;
    private bool _starved;

    /// <param name="initialResumeMs">The base resume cushion (the floor the decay halves toward).</param>
    /// <param name="silencePaddingFloorBlocks">The device padding, in render blocks, below which silence is queued. The default is the
    /// engine's own constant, so the model moves with it; <see cref="double.PositiveInfinity"/> models the FULL-DEPTH silence of the
    /// pre-WP-1b session (the device FIFO always topped up) — the comparison the tests pin.</param>
    public CushionStarvationPolicy(double initialResumeMs = DefaultInitialResumeMs,
                                   double silencePaddingFloorBlocks = PcmAudioSession.SilencePaddingFloorBlocks)
    {
        _initialMs = initialResumeMs;
        _floorBlocks = silencePaddingFloorBlocks;
        _resumeMs = initialResumeMs;
    }

    public string Name => "cushion";
    public double ResumeThresholdMs => _resumeMs;

    public void Reset() { _resumeMs = _initialMs; _lastIncidentMs = 0.0; _starved = false; }

    public StarvationStep Decide(in RtView view)
    {
        if (!_starved)
        {
            // The clock thread's decay: 30 s without an incident halves the cushion toward its floor, once per 30 s.
            if (_resumeMs > _initialMs && view.NowMs - _lastIncidentMs > DecayAfterMs)
            {
                _resumeMs = Math.Max(_initialMs, _resumeMs * 0.5);
                _lastIncidentMs = view.NowMs;
            }
            if (view.RingMs > AudioStressModel.Epsilon) return new StarvationStep(BlockAction.Content);
            _starved = true;
            return new StarvationStep(StarvedAction(in view), StarvationEvents.Incident | StarvationEvents.GrowAhead);
        }

        if (view.RingMs < _resumeMs) return new StarvationStep(StarvedAction(in view));

        _starved = false;
        _resumeMs = Math.Min(_resumeMs * 2.0, view.AheadMs);   // the next incident needs twice the cushion, never more than the ahead target
        _lastIncidentMs = view.NowMs;
        return new StarvationStep(BlockAction.Content, StarvationEvents.Resume);
    }

    // RenderSilence's floor: with at least SilencePaddingFloorBlocks render blocks already queued at the device nothing is submitted
    // (RenderBurst stops on a block that renders nothing); below it one block of silence goes in. A view whose block is smaller than the
    // RT block means the device is nearly full, which is far above the floor either way.
    private BlockAction StarvedAction(in RtView view)
        => view.DeviceMs >= _floorBlocks * view.BlockMs ? BlockAction.Withhold : BlockAction.Silence;
}

/// <summary>One starve episode, as the RT saw it.</summary>
/// <param name="AtMs">Model time of the incident edge (the ring ran empty).</param>
/// <param name="ResumeThresholdMs">The cushion the policy needed for THIS starve.</param>
/// <param name="StarvedMs">Edge to resume (RT-side time: includes the part the device still played from its buffer).</param>
/// <param name="Resumed">False when the run ended while still starved.</param>
public readonly record struct IncidentRecord(double AtMs, double ResumeThresholdMs, double StarvedMs, bool Resumed);

/// <summary>One audible gap: a maximal run of non-content at the speakers (silence the RT submitted, or a device hole).</summary>
public readonly record struct GapRecord(double StartMs, double DurationMs);

/// <summary>The outcome of one <see cref="AudioStressModel.Run"/>.</summary>
public sealed record StarvationVerdict(
    string Policy,
    string Scenario,
    double DurationMs,
    int Incidents,
    int Resumes,
    int DeviceStops,
    int AudibleGaps,
    double TotalGapMs,
    double LongestGapMs,
    double ContentPlayedMs,
    double PeakResumesPerSecond,
    double FinalResumeThresholdMs,
    double FinalRingTargetMs,
    IReadOnlyList<IncidentRecord> IncidentLog,
    IReadOnlyList<GapRecord> GapLog)
{
    /// <summary>More resumes than this inside any one second is the stutter loop of the audit's §0.2.</summary>
    public const double StutterResumesPerSecond = 2.0;

    /// <summary>The stutter loop: a policy that resumes, starves and resumes again faster than twice a second.</summary>
    public bool StutterLoop => PeakResumesPerSecond > StutterResumesPerSecond;

    /// <summary>One line for assertion messages.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Policy}/{Scenario}: incidents={Incidents} resumes={Resumes} deviceStops={DeviceStops} gaps={AudibleGaps} longestGap={LongestGapMs:0}ms totalGap={TotalGapMs:0}ms peakResumes/s={PeakResumesPerSecond:0}");
}

/// <summary>The model and its scenario factories. Static and stateless: every call builds its own queues, so parallel tests share nothing.</summary>
public static class AudioStressModel
{
    /// <summary>Comparison slack for the millisecond arithmetic.</summary>
    public const double Epsilon = 1e-9;

    /// <summary>Gaps shorter than this are rounding residue of the step arithmetic, not audible, and are not reported.</summary>
    public const double GapFloorMs = 1.0;

    /// <summary>The producer is absent from t = 0 for <paramref name="absenceMs"/>, then runs at <paramref name="healthyRate"/>×.</summary>
    public static StressScenario Absence(double absenceMs, double totalMs, double healthyRate = 4.0, StressConfig? config = null)
        => new(string.Create(CultureInfo.InvariantCulture, $"absence-{absenceMs:0}ms"), totalMs,
               ProducerSchedule.Steady(healthyRate).WithAbsence(0.0, absenceMs), config ?? StressConfig.Default);

    /// <summary>The producer runs at <paramref name="slowRate"/>× (below real time: a sustained shortfall) for
    /// <paramref name="slowMs"/>, then at <paramref name="recoverRate"/>×. A producer FASTER than real time never starves, so
    /// only a rate below 1 can reproduce the stutter loop.</summary>
    public static StressScenario SlowThenRecover(double slowRate, double slowMs, double recoverRate, double totalMs, StressConfig? config = null)
        => new(string.Create(CultureInfo.InvariantCulture, $"slow-{slowRate:0.##}x-for-{slowMs:0}ms"), totalMs,
               ProducerSchedule.Steady(recoverRate).With(0.0, slowMs, slowRate), config ?? StressConfig.Default);

    /// <summary>Both threads stop for <paramref name="pauseMs"/> at <paramref name="atMs"/> (a stop-the-world suspension); the device plays on.</summary>
    public static StressScenario GcPause(double pauseMs, double atMs, double totalMs, double healthyRate = 4.0, StressConfig? config = null)
        => new(string.Create(CultureInfo.InvariantCulture, $"pause-{pauseMs:0}ms"), totalMs, ProducerSchedule.Steady(healthyRate),
               config ?? StressConfig.Default, new[] { new SuspensionWindow(atMs, pauseMs) });

    /// <summary>Run <paramref name="scenario"/> against <paramref name="policy"/> (reset first) and return what was audible.</summary>
    public static StarvationVerdict Run(StressScenario scenario, IStarvationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(policy);
        StressConfig cfg = scenario.Config;
        if (cfg.BlockMs <= 0.0 || cfg.DeviceBufferMs <= 0.0 || cfg.TargetAheadMs <= 0.0 || cfg.MaxBlocksPerWake <= 0)
            throw new ArgumentOutOfRangeException(nameof(scenario), "the stress config needs a positive block, device buffer, ahead target and burst size");

        policy.Reset();
        double dt = cfg.BlockMs;
        long steps = (long)Math.Ceiling(scenario.DurationMs / dt);

        double ringTarget = cfg.TargetAheadMs;      // the ring's own target; GrowAhead raises it, the policy's cap (AheadMs) stays configured
        double ring = ringTarget;                   // steady state: ring full …
        bool awake = false;                         // … so the producer sleeps until the ring drops below low water
        var device = new DeviceFifo();
        device.Enqueue(true, cfg.DeviceBufferMs);   // … and so is the device buffer
        var speakers = new GapTracker();
        var incidents = new List<IncidentRecord>();
        var resumeTimes = new List<double>();
        int open = -1, resumes = 0, deviceStops = 0;

        for (long k = 0; k < steps; k++)
        {
            double t = k * dt;

            if (!scenario.IsSuspended(t))
            {
                // 1. the producer
                if (!awake && ring < cfg.LowWaterMs(ringTarget)) awake = true;
                if (awake)
                {
                    ring = Math.Min(ringTarget, ring + scenario.Producer.RateAt(t) * dt);
                    if (ring >= ringTarget - Epsilon) awake = false;
                }

                // 2. the RT: up to MaxBlocksPerWake blocks while the device has room
                for (int i = 0; i < cfg.MaxBlocksPerWake; i++)
                {
                    double room = cfg.DeviceBufferMs - device.LevelMs;
                    if (room <= Epsilon) break;
                    double chunk = Math.Min(dt, room);

                    var view = new RtView(t, ring, device.LevelMs, chunk, cfg.TargetAheadMs);
                    StarvationStep step = policy.Decide(in view);
                    StarvationEvents events = step.Events;

                    if ((events & StarvationEvents.Incident) != 0 && open < 0)
                    {
                        incidents.Add(new IncidentRecord(t, policy.ResumeThresholdMs, 0.0, false));
                        open = incidents.Count - 1;
                    }
                    if ((events & StarvationEvents.GrowAhead) != 0)
                        ringTarget = Math.Max(ringTarget, Math.Min(cfg.GrowAheadCapMs, ringTarget * 2.0));
                    if ((events & StarvationEvents.DeviceStop) != 0) deviceStops++;
                    if ((events & StarvationEvents.Resume) != 0)
                    {
                        resumes++;
                        resumeTimes.Add(t);
                        if (open >= 0)
                        {
                            IncidentRecord edge = incidents[open];
                            incidents[open] = edge with { StarvedMs = t - edge.AtMs, Resumed = true };
                            open = -1;
                        }
                    }

                    if (step.Action == BlockAction.Withhold) break;
                    if (step.Action == BlockAction.Silence) { device.Enqueue(false, chunk); continue; }

                    double take = Math.Min(chunk, ring);
                    if (take <= Epsilon) break;      // a policy asked for content from an empty ring: nothing to move
                    ring -= take;
                    device.Enqueue(true, take);
                }
            }

            // 3. the device plays one block; a dry device is a hole
            double remaining = dt;
            while (remaining > Epsilon)
            {
                if (!device.TryPeek(out bool content, out double runMs)) { speakers.Play(false, remaining); break; }
                double played = Math.Min(remaining, runMs);
                device.Take(played);
                speakers.Play(content, played);
                remaining -= played;
            }
        }

        speakers.Close();
        double endMs = steps * dt;
        if (open >= 0)
        {
            IncidentRecord last = incidents[open];
            incidents[open] = last with { StarvedMs = endMs - last.AtMs, Resumed = false };
        }

        double total = 0.0, longest = 0.0;
        for (int i = 0; i < speakers.Gaps.Count; i++)
        {
            total += speakers.Gaps[i].DurationMs;
            longest = Math.Max(longest, speakers.Gaps[i].DurationMs);
        }

        return new StarvationVerdict(
            policy.Name, scenario.Name, scenario.DurationMs,
            incidents.Count, resumes, deviceStops,
            speakers.Gaps.Count, total, longest, speakers.ContentMs,
            PeakPerSecond(resumeTimes), policy.ResumeThresholdMs, ringTarget,
            incidents, speakers.Gaps);
    }

    /// <summary>The most resumes inside any window of one second (a window starts at a resume).</summary>
    private static double PeakPerSecond(List<double> times)
    {
        int best = 0;
        for (int i = 0, j = 0; i < times.Count; i++)
        {
            if (j < i) j = i;
            while (j < times.Count && times[j] - times[i] < 1000.0) j++;
            best = Math.Max(best, j - i);
        }
        return best;
    }

    /// <summary>The device buffer: a FIFO of content/silence runs measured in milliseconds. Coalesces same-kind neighbours.</summary>
    private sealed class DeviceFifo
    {
        private struct Segment { public bool Content; public double Ms; }

        private Segment[] _segments = new Segment[16];
        private int _head, _count;

        public double LevelMs { get; private set; }

        public void Enqueue(bool content, double ms)
        {
            if (ms <= Epsilon) return;
            if (_count > 0)
            {
                ref Segment tail = ref _segments[(_head + _count - 1) % _segments.Length];
                if (tail.Content == content) { tail.Ms += ms; LevelMs += ms; return; }
            }
            if (_count == _segments.Length) Grow();
            _segments[(_head + _count) % _segments.Length] = new Segment { Content = content, Ms = ms };
            _count++;
            LevelMs += ms;
        }

        public bool TryPeek(out bool content, out double ms)
        {
            if (_count == 0) { content = false; ms = 0.0; return false; }
            content = _segments[_head].Content;
            ms = _segments[_head].Ms;
            return true;
        }

        public void Take(double ms)
        {
            ref Segment head = ref _segments[_head];
            head.Ms -= ms;
            LevelMs -= ms;
            if (head.Ms > Epsilon) return;
            LevelMs -= head.Ms;                       // drop the rounding residue with the segment
            _head = (_head + 1) % _segments.Length;
            _count--;
            if (_count == 0) LevelMs = 0.0;
        }

        private void Grow()
        {
            var bigger = new Segment[_segments.Length * 2];
            for (int i = 0; i < _count; i++) bigger[i] = _segments[(_head + i) % _segments.Length];
            _segments = bigger;
            _head = 0;
        }
    }

    /// <summary>The speakers: plays the device's output in time order and records the gaps.</summary>
    private sealed class GapTracker
    {
        private readonly List<GapRecord> _gaps = new();
        private double _clock, _gapStart, _gapMs;
        private bool _inGap;

        public IReadOnlyList<GapRecord> Gaps => _gaps;
        public double ContentMs { get; private set; }

        public void Play(bool content, double ms)
        {
            if (ms <= 0.0) return;
            if (content)
            {
                Close();
                ContentMs += ms;
            }
            else
            {
                if (!_inGap) { _inGap = true; _gapStart = _clock; _gapMs = 0.0; }
                _gapMs += ms;
            }
            _clock += ms;
        }

        public void Close()
        {
            if (!_inGap) return;
            _inGap = false;
            if (_gapMs >= GapFloorMs) _gaps.Add(new GapRecord(_gapStart, _gapMs));
        }
    }
}
